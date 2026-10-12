using System.Text;

using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Bus;

/// <summary>Spec 04 AC-17, AC-18 and the sender's submission, against the bare repository with the real git.</summary>
public sealed class ClaimAndReportTests : IAsyncLifetime
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName Central = MachineName.Parse("central");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private readonly BusRepoFixture _bus = new(Acme);

    private readonly HookedProcessRunner _hooks = TestHost.Hooks();

    private readonly RecordingLoggerProvider _log = new();

    private InMemorySecretStore _store = null!;

    private ServiceProvider _services = null!;

    public async ValueTask InitializeAsync()
    {
        await _bus.InitializeAsync();
        await _bus.SeedTenantAsync(Acme, [Central, HomeLaptop], CancellationToken.None);
        _store = await TestHost.StoreAsync(Acme, CancellationToken.None);
        _services = TestHost.Build(_bus, HomeLaptop, _store, _hooks, logging: _log);
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _bus.DisposeAsync();
    }

    [Fact]
    public async Task ClaimAsync_JobFromSender_MovedOnMainByteIdenticalInOneCommit()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        SeededJob seeded = await _bus.SeedSignedJobAsync(Central, HomeLaptop, TestKeys.Acme1, _store, "probe\n", ct: ct);
        string seedHead = await _bus.HeadShaAsync(ct);
        BusRepository repository = _services.GetRequiredService<BusRepository>();
        await _services.GetRequiredService<IBusProvider>().PullAsync(ct);
        var job = (AcceptedJob)await repository.VerifyIntakeAsync((await repository.ListNewJobsAsync(ct)).Single(), ct);

        // Act
        WriteOutcome outcome = await repository.ClaimAsync(job, ct);

        // Assert
        outcome.Should().Be(new Pushed(1));
        string claimed = BusPaths.EnvelopeFile(BusPaths.Claimed(Acme, HomeLaptop), seeded.Id);
        (await _bus.ShowBytesAsync(claimed, ct)).Should().Equal(seeded.Bytes);
        (await _bus.ExistsOnMainAsync(seeded.RelativePath, ct)).Should().BeFalse();
        (await _bus.LastCommitSubjectAsync(ct)).Should().Be($"acme claim {seeded.Id} central→home-laptop");
        (await _bus.RunGitAsync(_bus.BareDir, ["log", "-1", "--format=%an <%ae>", "main"], ct)).Should().Be("zyggy (home-laptop) <home-laptop@zyggy.org>");
        (await _bus.RunGitAsync(_bus.BareDir, ["rev-list", "--count", seedHead + "..main"], ct)).Should().Be("1");
        SecretSweep.AssertAbsent(_hooks.Calls.SelectMany(SecretSweep.TextsOf));
        SecretSweep.AssertAbsent(_log.Messages);
    }

    [Fact]
    public async Task ReportAsync_Done_ReportOnMainVerifiesAndClaimedJobGone()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        SeededJob seeded = await _bus.SeedSignedJobAsync(Central, HomeLaptop, TestKeys.Acme1, _store, "probe\n", ct: ct);
        BusRepository repository = _services.GetRequiredService<BusRepository>();
        await _services.GetRequiredService<IBusProvider>().PullAsync(ct);
        var job = (AcceptedJob)await repository.VerifyIntakeAsync((await repository.ListNewJobsAsync(ct)).Single(), ct);
        (await repository.ClaimAsync(job, ct)).Should().Be(new Pushed(1));
        string claimHead = await _bus.HeadShaAsync(ct);
        DateTimeOffset started = DateTimeOffset.UtcNow.AddSeconds(-2);
        var fields = new ReportFields(ReportStatus.Done, Started: started, Finished: started.AddSeconds(2), DurationSeconds: 2);

        // Act
        WriteOutcome outcome = await repository.ReportAsync(job, fields, Encoding.UTF8.GetBytes("probe ok\n"), TestKeys.Acme1, ct);

        // Assert
        outcome.Should().Be(new Pushed(1));
        string reportsFolder = BusPaths.Reports(Acme, HomeLaptop);
        var reports = (await _bus.RunGitAsync(_bus.BareDir, ["ls-tree", "--name-only", "main", reportsFolder + "/"], ct))
            .Split('\n').Where(p => p.EndsWith(".md", StringComparison.Ordinal)).ToList();
        string reportPath = reports.Should().ContainSingle().Subject;
        EnvelopeResult parsed = await new EnvelopeSigner(_store).ParseAndVerifyAsync(await _bus.ShowBytesAsync(reportPath, ct), Acme, ct);
        parsed.IsAccepted.Should().BeTrue(parsed.Rejection?.ToString());
        Model report = parsed.Envelope!;
        report.Header.InReplyTo.Should().Be(seeded.Id);
        report.Header.To.Should().Be(Central);
        report.Header.From.Should().Be(HomeLaptop);
        report.Report!.Status.Should().Be(ReportStatus.Done);
        report.Report.Started.Should().NotBeNull();
        report.Report.Finished.Should().NotBeNull();
        report.Report.DurationSeconds.Should().Be(2);
        (await _bus.ExistsOnMainAsync(BusPaths.EnvelopeFile(BusPaths.Claimed(Acme, HomeLaptop), seeded.Id), ct)).Should().BeFalse();
        (await _bus.LastCommitSubjectAsync(ct)).Should().Be($"acme report {report.Header.Id} home-laptop→central");
        (await _bus.RunGitAsync(_bus.BareDir, ["rev-list", "--count", claimHead + "..main"], ct)).Should().Be("1");
        SecretSweep.AssertAbsent(_hooks.Calls.SelectMany(SecretSweep.TextsOf));
        SecretSweep.AssertAbsent(_log.Messages);
    }

    [Fact]
    public async Task SubmitAsync_FromCentralClone_JobOnMainForHomeLaptop()
    {
        // Arrange: a repository for central working in the sender clone
        var ct = TestContext.Current.CancellationToken;
        await using ServiceProvider central = TestHost.Build(_bus, Central, _store, checkoutRoot: _bus.SenderDir);
        Model job = await TestEnvelopes.SignedJobAsync(Central, HomeLaptop, _store, TestKeys.Acme1, "probe\n", ct);

        // Act
        WriteOutcome outcome = await central.GetRequiredService<BusRepository>().SubmitAsync(job, ct);

        // Assert
        outcome.Should().Be(new Pushed(1));
        string path = BusPaths.EnvelopeFile(BusPaths.Jobs(Acme, HomeLaptop), job.Header.Id);
        (await _bus.ShowBytesAsync(path, ct)).Should().Equal(EnvelopeWriter.Serialize(job));
        (await _bus.LastCommitSubjectAsync(ct)).Should().Be($"acme job {job.Header.Id} central→home-laptop");
        (await _bus.RunGitAsync(_bus.BareDir, ["log", "-1", "--format=%an", "main"], ct)).Should().Be("zyggy (central)");
    }
}
