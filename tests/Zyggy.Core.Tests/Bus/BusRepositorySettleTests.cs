using System.Text;

using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Git;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Bus;

/// <summary>
/// Plan 41 A10 (§4 state machine row 2 and write rule bullet 3 as amended by W41-7/W41-8, §5 W41-9): Central's pass commit moves the
/// picked-up reports and contexts, removes each settled <c>items/</c> file and adds Central's signed report on it — one commit.
/// </summary>
public sealed class BusRepositorySettleTests : IDisposable
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly TenantId OtherOrg = TenantId.Parse("other-org");

    private static readonly MachineName Central = MachineName.Central;

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly BusRemote Remote = new("acme-org", "acme-bus", "main", new Uri("https://example.invalid/acme-org/acme-bus.git"), null);

    private static readonly DateTimeOffset T = new(2026, 10, 31, 14, 5, 0, TimeSpan.Zero);

    private readonly TempCheckout _checkout = new(Acme, Central);

    private readonly RecordingProcessRunner _processes = new();

    private readonly IBusProvider _provider = Substitute.For<IBusProvider>();

    private readonly FakeTimeProvider _clock = new(T);

    public BusRepositorySettleTests()
    {
        _provider.Remote.Returns(Remote);
        _provider.PullAsync(Arg.Any<CancellationToken>()).Returns(new BusSyncResult(true, string.Empty));
        _provider.PushAsync(Arg.Any<CancellationToken>()).Returns(new PushSucceeded());
    }

    public void Dispose() => _checkout.Dispose();

    [Fact]
    public async Task SettleAsync_MovesAndOneItem_OneCommit_SubjectCountsBoth_BodyMoveLinesThenFiledLine_ItemRemoved_ReportAdded()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        BusFile context = File(BusPaths.Context(Acme, HomeLaptop));
        BusFile item = File(BusPaths.Items(Acme, HomeLaptop));
        Model report = await ReportAsync(item.Id, ct);
        string reportPath = BusPaths.EnvelopeFile(BusPaths.Reports(Acme, Central), report.Header.Id);
        string archived = BusPaths.EnvelopeFile(BusPaths.Archive(Acme, 2026, 10), context.Id);

        // Act
        WriteOutcome outcome = await Repository().SettleAsync(
            [(context, T)],
            [new SettledItem(item, report, "filed archive/calizr/staging-restore-notes.txt")],
            ct);

        // Assert
        outcome.Should().Be(new Pushed(1));
        _processes.CallsOf("mv").Should().ContainSingle().Which.Arguments.Should().Equal("mv", "--", context.RelativePath, archived);
        _processes.CallsOf("rm").Should().ContainSingle().Which.Arguments.Should().EndWith(item.RelativePath);
        _processes.CallsOf("add").Should().ContainSingle().Which.Arguments.Should().EndWith(reportPath);
        var commit = _processes.CallsOf("commit").Should().ContainSingle().Subject;
        commit.StandardInput.Should().Be(
            "acme archive 2 central→archive\n\n"
            + context.RelativePath + " → " + archived + "\n"
            + item.RelativePath + " → filed archive/calizr/staging-restore-notes.txt");
        commit.Arguments.Should().Contain([context.RelativePath, archived, item.RelativePath, reportPath]);
        _checkout.Read(reportPath).Should().Equal(EnvelopeWriter.Serialize(report));
    }

    [Fact]
    public async Task SettleAsync_ItemOfAnotherTenant_Throws()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        BusFile item = File(BusPaths.Items(OtherOrg, HomeLaptop));
        Model report = await ReportAsync(item.Id, ct);

        // Act
        Func<Task> act = () => Repository().SettleAsync([], [new SettledItem(item, report, "refused too_large")], ct);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*another tenant*");
        _processes.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task SettleAsync_ReportNotInCentralsReports_Throws()
    {
        // Arrange: a report written by another machine can never go into Central's reports/ folder.
        var ct = TestContext.Current.CancellationToken;
        BusFile item = File(BusPaths.Items(Acme, HomeLaptop));
        Model report = await ReportAsync(item.Id, ct, from: HomeLaptop, to: Central);

        // Act
        Func<Task> act = () => Repository().SettleAsync([], [new SettledItem(item, report, "refused too_large")], ct);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("write rule:*");
        _processes.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task SettleAsync_NotCentral_Throws()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        BusFile item = File(BusPaths.Items(Acme, HomeLaptop));
        Model report = await ReportAsync(item.Id, ct);

        // Act
        Func<Task> act = () => Repository(HomeLaptop).SettleAsync([], [new SettledItem(item, report, "refused too_large")], ct);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("write rule: only central archives");
    }

    [Fact]
    public async Task ArchiveAsync_DelegatesUnchanged()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        BusFile report = File(BusPaths.Reports(Acme, HomeLaptop));
        string archived = BusPaths.EnvelopeFile(BusPaths.Archive(Acme, 2026, 10), report.Id);

        // Act
        WriteOutcome outcome = await Repository().ArchiveAsync([(report, T)], ct);

        // Assert
        outcome.Should().Be(new Pushed(1));
        _processes.CallsOf("rm").Should().BeEmpty();
        _processes.CallsOf("add").Should().BeEmpty();
        _processes.CallsOf("commit").Should().ContainSingle().Which.StandardInput.Should().Be(
            "acme archive 1 central→archive\n\n" + report.RelativePath + " → " + archived);
    }

    private static async Task<Model> ReportAsync(EnvelopeId item, CancellationToken ct, MachineName? from = null, MachineName? to = null) =>
        await TestEnvelopes.SignedReportAsync(
            from ?? Central, to ?? HomeLaptop, item, await TestKeys.StoreWithAsync(TestKeys.Acme1, ct), TestKeys.Acme1, "archived: archive/calizr/staging-restore-notes.txt\n", ct, created: T);

    private BusFile File(string folder)
    {
        var id = EnvelopeId.New(_clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        string path = BusPaths.EnvelopeFile(folder, id);
        _checkout.Drop(path, Encoding.UTF8.GetBytes("item"));
        return new BusFile(path, id);
    }

    private BusRepository Repository(MachineName? machine = null)
    {
        MachineName me = machine ?? Central;
        var git = new GitClient(_processes, new GitClientOptions { Identity = me == Central ? TestGitIdentities.Central : TestGitIdentities.HomeLaptop }, _clock);
        return new BusRepository(git, _provider, new BusIdentity(Acme, me), _checkout.Root, new EnvelopeSigner(new Zyggy.Core.Secrets.InMemorySecretStore()), _clock, () => 0.5, new RecordingLogger<BusRepository>());
    }
}
