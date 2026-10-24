using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Jobs;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Bus;

/// <summary>
/// Spec 07 AC-8 on the wire (founding spec §6 step 3 "the dispatcher leaves the job in <c>jobs/</c>"): the real listener with the §6
/// dispatcher as its gate and the job runner as its handler, the real <c>git</c> on the bare repository and the fake <c>claude</c>.
/// </summary>
public sealed class BusListenerGateRoundTripTests : IAsyncLifetime
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName Central = MachineName.Parse("central");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private readonly BusRepoFixture _bus = new(Acme);

    private readonly ScratchDirectory _scratch = new();

    private readonly HookedProcessRunner _hooks = TestHost.Hooks();

    private readonly RecordingLoggerProvider _log = new();

    private InMemorySecretStore _store = null!;

    private ScratchProject _free = null!;

    private ScratchProject _locked = null!;

    public async ValueTask InitializeAsync()
    {
        await _bus.InitializeAsync();
        await _bus.SeedTenantAsync(Acme, [Central, HomeLaptop], CancellationToken.None);
        _store = await TestHost.StoreAsync(Acme, CancellationToken.None);
        _free = await ScratchProject.CreateAsync(_bus, CancellationToken.None);
        _locked = await ScratchProject.CreateAsync(_bus, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        _free?.Dispose();
        _locked?.Dispose();
        _scratch.Dispose();
        await _bus.DisposeAsync();
    }

    [Fact]
    public async Task ProcessOnceAsync_TwoJobsOneProjectLocked_FreeOneReportedLockedOneStaysInJobsOnMain()
    {
        // Arrange: two jobs; the test holds the second project's lock like another process would
        var ct = TestContext.Current.CancellationToken;
        await using ServiceProvider services = Services();
        BusListener listener = services.GetRequiredService<BusListener>();
        (await listener.StartAsync(ct)).Should().Be(BusStartupResult.Clean);
        SeededJob freeJob = await _bus.SeedSignedJobAsync(Central, HomeLaptop, TestKeys.Acme1, _store, Body, fields: Fields(_free.Path), ct: ct);
        SeededJob lockedJob = await _bus.SeedSignedJobAsync(Central, HomeLaptop, TestKeys.Acme1, _store, Body, fields: Fields(_locked.Path), ct: ct);
        using ProjectLock? held = ProjectLock.TryAcquire(_locked.Path, EnvelopeId.New(DateTimeOffset.UtcNow), 4242, DateTimeOffset.UtcNow);
        held.Should().NotBeNull();
        (await services.GetRequiredService<IBusProvider>().PullAsync(ct)).Succeeded.Should().BeTrue();

        // Act
        int handled = await listener.ProcessOnceAsync(ct);
        IReadOnlyList<string> reports = await WaitForReportsAsync(1, ct);
        await listener.DrainAsync(ct);

        // Assert
        handled.Should().Be(1, "the locked job is left where it is, not handled");
        Model report = await ParseReportAsync(reports.Single(), ct);
        report.Header.InReplyTo.Should().Be(freeJob.Id);
        report.Report!.Status.Should().Be(ReportStatus.Done);
        (await _bus.ExistsOnMainAsync(lockedJob.RelativePath, ct)).Should().BeTrue("the deferred job stays at its jobs/ path on main");
        (await _bus.ExistsOnMainAsync(BusPaths.EnvelopeFile(BusPaths.Claimed(Acme, HomeLaptop), lockedJob.Id), ct)).Should().BeFalse();
        _log.Messages.Where(m => m == $"job {lockedJob.Id.Value} deferred: ProjectBusy").Should().ContainSingle();
        File.Exists(CapturePathOf(_locked)).Should().BeFalse("the fake never started for the locked project");
        File.Exists(CapturePathOf(_free)).Should().BeTrue();
        SecretSweep.AssertAbsent(_hooks.Calls.SelectMany(SecretSweep.TextsOf));
        SecretSweep.AssertAbsent(_log.Messages);
    }

    [Fact]
    public async Task ProcessOnceAsync_LockReleased_NextPassClaimsIt()
    {
        // Arrange: one job deferred on the first pass because its project is locked
        var ct = TestContext.Current.CancellationToken;
        await using ServiceProvider services = Services();
        BusListener listener = services.GetRequiredService<BusListener>();
        (await listener.StartAsync(ct)).Should().Be(BusStartupResult.Clean);
        SeededJob job = await _bus.SeedSignedJobAsync(Central, HomeLaptop, TestKeys.Acme1, _store, Body, fields: Fields(_locked.Path), ct: ct);
        ProjectLock? held = ProjectLock.TryAcquire(_locked.Path, EnvelopeId.New(DateTimeOffset.UtcNow), 4242, DateTimeOffset.UtcNow);
        held.Should().NotBeNull();
        (await services.GetRequiredService<IBusProvider>().PullAsync(ct)).Succeeded.Should().BeTrue();
        (await listener.ProcessOnceAsync(ct)).Should().Be(0);

        // Act: the lock goes away; the next pass claims and runs it
        held!.Dispose();
        int handled = await listener.ProcessOnceAsync(ct);
        IReadOnlyList<string> reports = await WaitForReportsAsync(1, ct);
        await listener.DrainAsync(ct);

        // Assert
        handled.Should().Be(1);
        (await ParseReportAsync(reports.Single(), ct)).Header.InReplyTo.Should().Be(job.Id);
        (await _bus.ExistsOnMainAsync(job.RelativePath, ct)).Should().BeFalse();
        _log.Messages.Where(m => m == $"job {job.Id.Value} deferred: ProjectBusy").Should().ContainSingle();
        _log.Messages.Where(m => m == $"job {job.Id.Value} admitted after deferral").Should().ContainSingle();
    }

    private static string Body => "Read README.md and finish with a REPORT section; change nothing.\n";

    private static JobFields Fields(string project) => new(project, TimeoutMinutes: 2, AllowedTools: ["Read"]);

    private static string CapturePathOf(ScratchProject project) => Path.Combine(project.Path, "capture.bin");

    private ServiceProvider Services() =>
        TestHost.BuildRunnerListener(
            _bus,
            HomeLaptop,
            _store,
            FakeClaude.ExecutablePath,
            TestHost.NodeConfigurationFor(_bus, HomeLaptop),
            o =>
            {
                o.NodeDir = Path.Combine(_scratch.Path, "node");
                o.TimeoutOverride = TimeSpan.FromSeconds(30);
                o.ModelEnvironment = new Dictionary<string, string> { ["ZYGGY_FAKE_CLAUDE_SCENARIO"] = "done", ["ZYGGY_FAKE_CLAUDE_CAPTURE"] = "capture.bin" };
            },
            _hooks,
            _log,
            withDispatcher: true);

    private async Task<IReadOnlyList<string>> WaitForReportsAsync(int count, CancellationToken ct)
    {
        List<string> reports = [];
        for (int i = 0; i < 120 && reports.Count < count; i++)
        {
            await Task.Delay(250, ct);
            string output = await _bus.RunGitAsync(_bus.BareDir, ["ls-tree", "--name-only", "main", BusPaths.Reports(Acme, HomeLaptop) + "/"], ct);
            reports = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(p => p.EndsWith(".md", StringComparison.Ordinal)).ToList();
        }

        return reports;
    }

    private async Task<Model> ParseReportAsync(string path, CancellationToken ct)
    {
        EnvelopeResult parsed = await new EnvelopeSigner(_store).ParseAndVerifyAsync(await _bus.ShowBytesAsync(path, ct), Acme, ct);
        parsed.IsAccepted.Should().BeTrue(parsed.Rejection?.ToString());
        return parsed.Envelope!;
    }
}
