using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Git;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Bus;

// AC-2 (status part): a machine adds, replaces and removes only its own status files; every other write is refused before git runs.
public sealed class WorkStatusRepositoryTests : IAsyncLifetime
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly MachineName WorkLaptop = MachineName.Parse("work-laptop");

    private static readonly BusIdentity Me = new(Acme, HomeLaptop);

    private static readonly DateTimeOffset T = new(2026, 11, 15, 10, 42, 0, TimeSpan.Zero);

    private readonly TempCheckout _checkout = new(Acme, HomeLaptop);

    private readonly RecordingProcessRunner _processes = new();

    private readonly IBusProvider _provider = Substitute.For<IBusProvider>();

    private readonly RecordingLogger<BusRepository> _log = new();

    private readonly FakeTimeProvider _clock = new(T);

    private InMemorySecretStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _provider.PullAsync(Arg.Any<CancellationToken>()).Returns(new BusSyncResult(true, string.Empty));
        _provider.PushAsync(Arg.Any<CancellationToken>()).Returns(new PushSucceeded());
        _store = await TestKeys.StoreWithBothAsync(CancellationToken.None);
    }

    public ValueTask DisposeAsync()
    {
        _checkout.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public void BusPaths_Status_BuildsAndParsesTheMachineStatusFolder()
    {
        // Arrange
        var id = EnvelopeId.New(T);

        // Act
        string path = BusPaths.EnvelopeFile(BusPaths.Status(Acme, HomeLaptop), id);
        bool parsed = BusPaths.TryParse(path, out BusLocation? location);

        // Assert
        path.Should().Be($"tenants/acme/nodes/home-laptop/status/{id.Value}.md");
        parsed.Should().BeTrue();
        location.Should().Be(new BusLocation(Acme, HomeLaptop, BusFolder.Status, id));
    }

    [Fact]
    public async Task PublishStatusAsync_SignedStatusFromMe_FileUnderMyStatusFolder_OneCommitWithSubject()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model status = await SignedAsync(HomeLaptop, "5b3fd3ac", T, ct);
        string path = PathOf(HomeLaptop, status);

        // Act
        WriteOutcome outcome = await Repository().PublishStatusAsync(status, ct);

        // Assert
        outcome.Should().Be(new Pushed(1));
        _checkout.Read(path).Should().Equal(EnvelopeWriter.Serialize(status));
        _processes.CallsOf("add").Should().ContainSingle().Which.Arguments.Should().Contain(path);
        _processes.CallsOf("rm").Should().BeEmpty();
        _processes.CallsOf("commit").Should().ContainSingle().Which.StandardInput.Should().StartWith($"acme status {status.Header.Id.Value} home-laptop→central");
    }

    [Fact]
    public async Task PublishStatusAsync_SameSessionHasAnOlderFile_RemovedInTheSameCommit_OtherSessionKept()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model previous = await SignedAsync(HomeLaptop, "5b3fd3ac", T.AddMinutes(-20), ct);
        Model other = await SignedAsync(HomeLaptop, "77aa00bb", T.AddMinutes(-5), ct);
        _checkout.Drop(PathOf(HomeLaptop, previous), EnvelopeWriter.Serialize(previous));
        _checkout.Drop(PathOf(HomeLaptop, other), EnvelopeWriter.Serialize(other));
        Model status = await SignedAsync(HomeLaptop, "5b3fd3ac", T, ct);

        // Act
        WriteOutcome outcome = await Repository().PublishStatusAsync(status, ct);

        // Assert
        outcome.Should().Be(new Pushed(1));
        _processes.CallsOf("rm").Should().ContainSingle().Which.Arguments.Should().Contain(PathOf(HomeLaptop, previous)).And.NotContain(PathOf(HomeLaptop, other));
        _processes.CallsOf("commit").Should().ContainSingle();
    }

    [Fact]
    public async Task PublishStatusAsync_OwnFileOlderThan24Hours_RemovedWhateverItsSession()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model crashed = await SignedAsync(HomeLaptop, "0000dead", T.AddHours(-24).AddSeconds(-1), ct);
        Model young = await SignedAsync(HomeLaptop, "77aa00bb", T.AddHours(-24).AddSeconds(1), ct);
        _checkout.Drop(PathOf(HomeLaptop, crashed), EnvelopeWriter.Serialize(crashed));
        _checkout.Drop(PathOf(HomeLaptop, young), EnvelopeWriter.Serialize(young));
        Model status = await SignedAsync(HomeLaptop, "5b3fd3ac", T, ct);

        // Act
        await Repository().PublishStatusAsync(status, ct);

        // Assert
        _processes.CallsOf("rm").Should().ContainSingle().Which.Arguments.Should().Contain(PathOf(HomeLaptop, crashed)).And.NotContain(PathOf(HomeLaptop, young));
    }

    [Fact]
    public async Task PublishStatusAsync_AnotherMachinesOldFiles_NeverTouched()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model foreign = await SignedAsync(WorkLaptop, "5b3fd3ac", T.AddDays(-3), ct);
        _checkout.Drop(PathOf(WorkLaptop, foreign), EnvelopeWriter.Serialize(foreign));
        Model status = await SignedAsync(HomeLaptop, "5b3fd3ac", T, ct);

        // Act
        await Repository().PublishStatusAsync(status, ct);

        // Assert
        _processes.CallsOf("rm").Should().BeEmpty();
        _checkout.Exists(PathOf(WorkLaptop, foreign)).Should().BeTrue();
    }

    [Fact]
    public async Task PublishStatusAsync_FromAnotherMachine_ThrowsWriteRule_NoGitCall()
    {
        // Arrange: a status signed as work-laptop, published by home-laptop's repository
        var ct = TestContext.Current.CancellationToken;
        Model status = await SignedAsync(WorkLaptop, "5b3fd3ac", T, ct);

        // Act
        Func<Task> act = () => Repository().PublishStatusAsync(status, ct);

        // Assert
        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("write rule:*another machine*");
        _processes.Calls.Should().BeEmpty();
        _checkout.FilesUnder(BusPaths.Status(Acme, WorkLaptop)).Should().BeEmpty();
    }

    [Fact]
    public async Task PublishStatusAsync_AnotherTenant_ThrowsWriteRule_NoGitCall()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model status = Unsigned(HomeLaptop, "5b3fd3ac", T, TenantId.Parse("globex"));

        // Act
        Func<Task> act = () => Repository().PublishStatusAsync(status, ct);

        // Assert
        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("write rule:*another tenant*");
        _processes.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task PublishStatusAsync_Unsigned_ThrowsWriteRule_NoGitCall()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model status = Unsigned(HomeLaptop, "5b3fd3ac", T, Acme);

        // Act
        Func<Task> act = () => Repository().PublishStatusAsync(status, ct);

        // Assert
        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("write rule:*unsigned*");
        _processes.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task PublishStatusAsync_NotAStatusEnvelope_ThrowsWriteRule_NoGitCall()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model job = await TestEnvelopes.SignedJobAsync(HomeLaptop, MachineName.Central, _store, TestKeys.Acme1, "probe\n", ct);

        // Act
        Func<Task> act = () => Repository().PublishStatusAsync(job, ct);

        // Assert
        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("write rule:*not a status*");
        _processes.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task RemoveStatusAsync_SessionHasAFile_RemovedInOneCommit_OtherSessionKept()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model mine = await SignedAsync(HomeLaptop, "5b3fd3ac", T.AddMinutes(-20), ct);
        Model other = await SignedAsync(HomeLaptop, "77aa00bb", T.AddMinutes(-5), ct);
        _checkout.Drop(PathOf(HomeLaptop, mine), EnvelopeWriter.Serialize(mine));
        _checkout.Drop(PathOf(HomeLaptop, other), EnvelopeWriter.Serialize(other));

        // Act
        WriteOutcome outcome = await Repository().RemoveStatusAsync("5b3fd3ac", ct);

        // Assert
        outcome.Should().Be(new Pushed(1));
        _processes.CallsOf("add").Should().BeEmpty();
        _processes.CallsOf("rm").Should().ContainSingle().Which.Arguments.Should().Contain(PathOf(HomeLaptop, mine)).And.NotContain(PathOf(HomeLaptop, other));
        _processes.CallsOf("commit").Should().ContainSingle().Which.StandardInput.Should().StartWith($"acme status {mine.Header.Id.Value} home-laptop→central");
    }

    [Fact]
    public async Task RemoveStatusAsync_SessionHasNoFile_NothingWritten()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model foreign = await SignedAsync(WorkLaptop, "5b3fd3ac", T, ct);
        _checkout.Drop(PathOf(WorkLaptop, foreign), EnvelopeWriter.Serialize(foreign));

        // Act
        WriteOutcome outcome = await Repository().RemoveStatusAsync("5b3fd3ac", ct);

        // Assert
        outcome.Should().Be(new Pushed(0));
        _processes.Calls.Should().BeEmpty();
        _provider.ReceivedCalls().Should().BeEmpty();
    }

    private static string PathOf(MachineName machine, Model status) => BusPaths.EnvelopeFile(BusPaths.Status(Acme, machine), status.Header.Id);

    private static Model Unsigned(MachineName from, string session, DateTimeOffset at, TenantId tenant) =>
        Model.CreateStatus(new EnvelopeHeader(tenant, EnvelopeId.New(at), from, MachineName.Central, at), new StatusFields("calizr", session, at.AddHours(-1), at));

    private async Task<Model> SignedAsync(MachineName from, string session, DateTimeOffset at, CancellationToken ct)
    {
        EnvelopeResult signed = await new EnvelopeSigner(_store).SignAsync(Unsigned(from, session, at, Acme), TestKeys.Acme1, ct);
        return signed.Envelope!;
    }

    private BusRepository Repository()
    {
        var git = new GitClient(_processes, new GitClientOptions { Identity = new GitIdentity("zyggy (home-laptop)", "home-laptop@zyggy.org") }, _clock);
        return new BusRepository(git, _provider, Me, _checkout.Root, new EnvelopeSigner(_store), _clock, () => 0.5, _log);
    }
}
