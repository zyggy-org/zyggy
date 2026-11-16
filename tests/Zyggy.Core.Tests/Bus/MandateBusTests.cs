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

// AC-2 (mandates): only Central writes a mandate and moves it to revoked/; every refusal happens before any git call.
public sealed class MandateBusTests : IAsyncLifetime
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName Central = MachineName.Central;

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly DateTimeOffset T = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);

    private readonly TempCheckout _checkout = new(Acme, Central);

    private readonly RecordingProcessRunner _processes = new();

    private readonly IBusProvider _provider = Substitute.For<IBusProvider>();

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
    public void BusPaths_MandatesAndRevoked_BuildAndParse()
    {
        // Arrange
        var id = EnvelopeId.New(T);

        // Act
        string live = BusPaths.EnvelopeFile(BusPaths.Mandates(Acme), id);
        string revoked = BusPaths.EnvelopeFile(BusPaths.RevokedMandates(Acme), id);
        bool liveParsed = BusPaths.TryParse(live, out BusLocation? liveAt);
        bool revokedParsed = BusPaths.TryParse(revoked, out BusLocation? revokedAt);

        // Assert
        live.Should().Be($"tenants/acme/mandates/{id.Value}.md");
        revoked.Should().Be($"tenants/acme/mandates/revoked/{id.Value}.md");
        (liveParsed, revokedParsed).Should().Be((true, true));
        liveAt.Should().Be(new BusLocation(Acme, null, BusFolder.Mandates, id));
        revokedAt.Should().Be(new BusLocation(Acme, null, BusFolder.RevokedMandates, id));
    }

    [Theory]
    [InlineData("tenants/acme/mandates/notes.md")]
    [InlineData("tenants/acme/mandates/revoked/deeper/01M7F1M0FFFFFFFFFFFFFFFFFF.md")]
    [InlineData("tenants/acme/mandates/other/01M7F1M0FFFFFFFFFFFFFFFFFF.md")]
    public void BusPaths_OtherPathsUnderMandates_NotABusPath(string path)
    {
        // Act
        bool parsed = BusPaths.TryParse(path, out _);

        // Assert
        parsed.Should().BeFalse();
    }

    [Fact]
    public async Task PublishMandateAsync_OnCentral_FileUnderMandates_OneCommitNamingTheMachine()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model mandate = await MandateAsync(Central, ct);
        string path = BusPaths.EnvelopeFile(BusPaths.Mandates(Acme), mandate.Header.Id);

        // Act
        WriteOutcome outcome = await Repository(Central).PublishMandateAsync(mandate, ct);

        // Assert
        outcome.Should().Be(new Pushed(1));
        _checkout.Read(path).Should().Equal(EnvelopeWriter.Serialize(mandate));
        _processes.CallsOf("commit").Should().ContainSingle().Which.StandardInput.Should().StartWith($"acme mandate {mandate.Header.Id.Value} central→home-laptop");
    }

    [Fact]
    public async Task PublishMandateAsync_OnALaptop_ThrowsWriteRule_NoGitCall()
    {
        // Arrange: a mandate that verifies, published by home-laptop's repository
        var ct = TestContext.Current.CancellationToken;
        Model mandate = await MandateAsync(Central, ct);

        // Act
        Func<Task> act = () => Repository(HomeLaptop).PublishMandateAsync(mandate, ct);

        // Assert
        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("write rule:*only central*");
        _processes.Calls.Should().BeEmpty();
        _checkout.FilesUnder(BusPaths.Mandates(Acme)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("unsigned")]
    [InlineData("a-decision")]
    public async Task PublishMandateAsync_NotASignedMandateOfThisTenant_ThrowsWriteRule_NoGitCall(string fault)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model envelope = fault switch
        {
            "unsigned" => Model.CreateMandate(new EnvelopeHeader(Acme, EnvelopeId.New(T), Central, HomeLaptop, T), Fields()),
            _ => (await new EnvelopeSigner(_store).SignAsync(
                Model.CreateDecision(
                    new EnvelopeHeader(Acme, EnvelopeId.New(T), Central, HomeLaptop, T, EnvelopePriority.Normal, EnvelopeId.New(T.AddMinutes(-1))),
                    new DecisionFields(DecisionKind.Answer, DecisionBasis.Reported, "nothing to undo"),
                    "Use central.\n"u8.ToArray()),
                TestKeys.Acme1,
                ct)).Envelope!,
        };

        // Act
        Func<Task> act = () => Repository(Central).PublishMandateAsync(envelope, ct);

        // Assert
        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("write rule:*");
        _processes.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task RevokeMandateAsync_OnCentral_FileMovedToRevoked_OneCommit()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model mandate = await MandateAsync(Central, ct);
        string live = BusPaths.EnvelopeFile(BusPaths.Mandates(Acme), mandate.Header.Id);
        string revoked = BusPaths.EnvelopeFile(BusPaths.RevokedMandates(Acme), mandate.Header.Id);
        _checkout.Drop(live, EnvelopeWriter.Serialize(mandate));

        // Act
        WriteOutcome outcome = await Repository(Central).RevokeMandateAsync(mandate, ct);

        // Assert
        outcome.Should().Be(new Pushed(1));
        _processes.CallsOf("mv").Should().ContainSingle().Which.Arguments.Should().Contain(live).And.Contain(revoked);
        _processes.CallsOf("commit").Should().ContainSingle().Which.StandardInput.Should().StartWith($"acme revoke {mandate.Header.Id.Value} central→home-laptop");
    }

    [Fact]
    public async Task RevokeMandateAsync_OnALaptop_ThrowsWriteRule_NoGitCall()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model mandate = await MandateAsync(Central, ct);
        _checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Mandates(Acme), mandate.Header.Id), EnvelopeWriter.Serialize(mandate));

        // Act
        Func<Task> act = () => Repository(HomeLaptop).RevokeMandateAsync(mandate, ct);

        // Assert
        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("write rule:*only central*");
        _processes.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task RevokeMandateAsync_NoLiveFile_ThrowsWriteRule_NoGitCall()
    {
        // Arrange: already revoked, or never published
        var ct = TestContext.Current.CancellationToken;
        Model mandate = await MandateAsync(Central, ct);

        // Act
        Func<Task> act = () => Repository(Central).RevokeMandateAsync(mandate, ct);

        // Assert
        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("write rule:*not in mandates*");
        _processes.Calls.Should().BeEmpty();
    }

    private static MandateFields Fields() => new("calizr", "Build the booking pool", ["Read"], 3, T.AddDays(1));

    private async Task<Model> MandateAsync(MachineName from, CancellationToken ct)
    {
        Model mandate = Model.CreateMandate(new EnvelopeHeader(Acme, EnvelopeId.New(T), from, HomeLaptop, T), Fields());
        return (await new EnvelopeSigner(_store).SignAsync(mandate, TestKeys.Acme1, ct)).Envelope!;
    }

    private BusRepository Repository(MachineName me)
    {
        var git = new GitClient(_processes, new GitClientOptions { Identity = new GitIdentity("zyggy (" + me.Value + ")", me.Value + "@zyggy.org") }, _clock);
        return new BusRepository(git, _provider, new BusIdentity(Acme, me), _checkout.Root, new EnvelopeSigner(_store), _clock, () => 0.5, new RecordingLogger<BusRepository>());
    }
}
