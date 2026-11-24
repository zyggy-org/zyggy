using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Processes;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Cli;
using Zyggy.Integration.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Bus;

/// <summary>
/// Spec 61 slice 2 (AC-4, AC-5): a job is never run a second time. The job's file back in <c>jobs/</c> while its run is alive is claimed
/// again without a second model start; a claim resumed after a restart whose result is kept in the node folder is answered by that report.
/// Two checkouts of one bare repository (the home laptop's listener with the production job runner and <c>tools/fake-claude</c>; Central's
/// <c>zyggy sweep</c> in process), real git. AC-6 (the <c>done</c> ids) is proven by the listener's unit tests.
/// </summary>
public sealed class NoSecondRunTests : IAsyncLifetime
{
    /// <summary>How far ahead Central's clock runs: a claim committed just now is past its 2-minute timeout + 15 min.</summary>
    private static readonly TimeSpan Ahead = TimeSpan.FromHours(1);

    private static readonly ProcessResult CommitRefused = new(128, string.Empty, "fatal: unable to write new index file", false, false, false, TimeSpan.Zero);

    private readonly HookedProcessRunner _hooks = TestHost.Hooks();

    private CliHarness _harness = null!;

    private ScratchProject _project = null!;

    private ServiceProvider _node = null!;

    private string NodeDir => _harness.NodeDirOf("home-laptop-listener");

    public async ValueTask InitializeAsync()
    {
        _harness = await CliHarness.CreateAsync(CancellationToken.None);
        _project = await ScratchProject.CreateAsync(_harness.Bus, CancellationToken.None);
        _node = await BuildNodeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _node.DisposeAsync();
        _project?.Dispose();
        await _harness.DisposeAsync();
    }

    // AC-4: Central re-queues the claim while the model runs and the node's next pass sees the file in jobs/: it is claimed again, the model
    // is started once in total, and the live run's report answers it.
    [Fact]
    public async Task Intake_FileBackWhileRunAlive_ReclaimedModelStartedOnce()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model job = await SeedJobAsync(ct);
        BusListener listener = _node.GetRequiredService<BusListener>();
        BusRepository repository = _node.GetRequiredService<BusRepository>();
        bool requeued = false;
        _hooks.BeforeRun = async spec =>
        {
            if (!requeued && spec.FileName == FakeClaude.ExecutablePath)
            {
                requeued = true;
                (await SweepAsync(ct)).Exit.Should().Be(0);
                (await repository.PullAsync(ct)).Succeeded.Should().BeTrue();
                await listener.ProcessOnceAsync(ct);
            }
        };

        // Act
        (await listener.StartAsync(ct)).Should().Be(BusStartupResult.Clean);

        // Assert
        requeued.Should().BeTrue();
        await AssertReportedOnceAsync(job, ct);
        string subjects = await _harness.Bus.RunGitAsync(_harness.Bus.BareDir, ["log", "--format=%s", "main"], ct);
        subjects.Split('\n').Count(s => s.StartsWith("acme claim " + job.Header.Id.Value, StringComparison.Ordinal)).Should().Be(2, "claimed, re-queued, claimed again");
    }

    // AC-5: the run ended but its report could not be committed; the node restarts with the claim still in jobs/claimed/ and the result
    // kept: the resumed claim is not run, and the kept report answers it once the bus write works again.
    [Fact]
    public async Task Resume_KeptResultAfterRestart_ReportedWithoutRun()
    {
        // Arrange: every report commit is refused through the first start and the restart
        var ct = TestContext.Current.CancellationToken;
        Model job = await SeedJobAsync(ct);
        bool refuse = true;
        _hooks.Override = spec => refuse && IsReportCommit(spec) ? CommitRefused : null;
        (await _node.GetRequiredService<BusListener>().StartAsync(ct)).Should().Be(BusStartupResult.Clean);
        KeptFiles().Should().ContainSingle("the run ended and its report could not be written");
        (await _harness.Bus.ExistsOnMainAsync(ClaimedPath(job), ct)).Should().BeTrue();
        await _node.DisposeAsync();
        _node = await BuildNodeAsync();
        BusListener restarted = _node.GetRequiredService<BusListener>();
        (await restarted.StartAsync(ct)).Should().Be(BusStartupResult.Clean);

        // Act
        await restarted.ResumeClaimedAsync(ct);
        refuse = false;
        await restarted.ProcessOnceAsync(ct);

        // Assert
        await AssertReportedOnceAsync(job, ct);
    }

    private static bool IsReportCommit(ProcessSpec spec) =>
        HookedProcessRunner.SubCommand(spec) == "commit" && (spec.StandardInput?.StartsWith("acme report ", StringComparison.Ordinal) ?? false);

    private static string QueuedPath(Model job) => BusPaths.EnvelopeFile(BusPaths.Jobs(CliHarness.Acme, CliHarness.HomeLaptop), job.Header.Id);

    private static string ClaimedPath(Model job) => BusPaths.EnvelopeFile(BusPaths.Claimed(CliHarness.Acme, CliHarness.HomeLaptop), job.Header.Id);

    private async Task<ServiceProvider> BuildNodeAsync() =>
        TestHost.BuildRunnerListener(
            _harness.Bus,
            CliHarness.HomeLaptop,
            await TestHost.StoreAsync(CliHarness.Acme, CancellationToken.None),
            FakeClaude.ExecutablePath,
            TestHost.NodeConfigurationFor(_harness.Bus, CliHarness.HomeLaptop),
            o =>
            {
                o.NodeDir = NodeDir;
                o.TimeoutOverride = TimeSpan.FromSeconds(30);
                o.ModelEnvironment = new Dictionary<string, string> { ["ZYGGY_FAKE_CLAUDE_SCENARIO"] = "done" };
            },
            hooks: _hooks);

    // The node's one report answers the job, done; the job's file is gone from jobs/ and jobs/claimed/; the model ran once; nothing stays kept.
    private async Task AssertReportedOnceAsync(Model job, CancellationToken ct)
    {
        string path = (await ReportsOnMainAsync(ct)).Should().ContainSingle().Subject;
        Model report = await _harness.ParseOnMainAsync(path, ct);
        report.Header.InReplyTo.Should().Be(job.Header.Id);
        report.Report!.Status.Should().Be(ReportStatus.Done);
        (await _harness.Bus.ExistsOnMainAsync(QueuedPath(job), ct)).Should().BeFalse();
        (await _harness.Bus.ExistsOnMainAsync(ClaimedPath(job), ct)).Should().BeFalse();
        _hooks.Calls.Count(c => c.FileName == FakeClaude.ExecutablePath).Should().Be(1, "the job is never run a second time");
        KeptFiles().Should().BeEmpty();
    }

    private string[] KeptFiles()
    {
        string folder = KeptReports.DirectoryFor(NodeDir, CliHarness.Acme);
        return Directory.Exists(folder) ? Directory.GetFiles(folder, "*.md") : [];
    }

    // A queued job central → home-laptop for the scratch project, with a 2-minute timeout.
    private async Task<Model> SeedJobAsync(CancellationToken ct)
    {
        Model job = await TestEnvelopes.SignedJobAsync(
            CliHarness.Central,
            CliHarness.HomeLaptop,
            _harness.Store,
            TestKeys.Acme1,
            "Read README.md and finish with a REPORT section; change nothing.\n",
            ct,
            fields: new JobFields(_project.Path, TimeoutMinutes: 2, AllowedTools: ["Read"], Attempt: 1));
        await _harness.Bus.SeedRawFileAsync(QueuedPath(job), EnvelopeWriter.Serialize(job), "acme job " + job.Header.Id.Value + " central→home-laptop", ct);
        return job;
    }

    private async Task<IReadOnlyList<string>> ReportsOnMainAsync(CancellationToken ct)
    {
        string output = await _harness.Bus.RunGitAsync(_harness.Bus.BareDir, ["ls-tree", "--name-only", "main", BusPaths.Reports(CliHarness.Acme, CliHarness.HomeLaptop) + "/"], ct);
        return [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(p => p.EndsWith(".md", StringComparison.Ordinal))];
    }

    private Task<(int Exit, VerbConsole Console)> SweepAsync(CancellationToken ct) =>
        _harness.RunAsync(["sweep"], _harness.CentralNodeJson, more: s => s.AddSingleton<TimeProvider>(new OffsetClock(Ahead)), ct: ct);

    /// <summary>The system clock moved ahead by a fixed amount; timers stay real.</summary>
    private sealed class OffsetClock(TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + offset;
    }
}
