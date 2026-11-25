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
/// Spec 61 slice 1 (AC-1, AC-2, AC-3): a finished run's report reaches the bus when Central re-queued or answered the claim while the run
/// went on, and after a failed write; the job is run once. Two checkouts of one bare repository (the home laptop's listener with the
/// production job runner and <c>tools/fake-claude</c>; Central's <c>zyggy sweep</c> in process), real git. Reverses spec 17 D3.
/// </summary>
public sealed class LateReportTests : IAsyncLifetime
{
    /// <summary>How far ahead Central's clock runs: a claim committed just now is past its 2-minute timeout + 15 min.</summary>
    private static readonly TimeSpan Ahead = TimeSpan.FromHours(1);

    private static readonly ProcessResult PushRefused = new(1, string.Empty, "! [rejected] main -> main (fetch first)", false, false, false, TimeSpan.Zero);

    private readonly HookedProcessRunner _hooks = TestHost.Hooks();

    private CliHarness _harness = null!;

    private ScratchProject _project = null!;

    private ServiceProvider _node = null!;

    private string NodeDir => _harness.NodeDirOf("home-laptop-listener");

    public async ValueTask InitializeAsync()
    {
        _harness = await CliHarness.CreateAsync(CancellationToken.None);
        _project = await ScratchProject.CreateAsync(_harness.Bus, CancellationToken.None);
        _node = TestHost.BuildRunnerListener(
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
    }

    public async ValueTask DisposeAsync()
    {
        await _node.DisposeAsync();
        _project?.Dispose();
        await _harness.DisposeAsync();
    }

    // AC-2: Central re-queues the claim while the model runs; the report removes the queued file in its commit, and the next pass runs nothing.
    [Fact]
    public async Task Report_RequeuedWhileRunning_ReportOnRemoteJobGoneModelRanOnce()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model job = await SeedJobAsync(attempt: 1, ct);
        _hooks.BeforeRun = SweepAtModelStart(ct);
        BusListener listener = _node.GetRequiredService<BusListener>();

        // Act: the start runs the job; the next pass sees the bus after the report
        (await listener.StartAsync(ct)).Should().Be(BusStartupResult.Clean);
        await listener.ProcessOnceAsync(ct);

        // Assert
        await AssertReportedOnceAsync(job, ct);
        (await _harness.Bus.LastCommitSubjectAsync(ct)).Should().StartWith("acme report ");
    }

    // AC-2: the re-queue is pushed between the node's report commit and its push; the dropped report is kept and the next pass writes it.
    [Fact]
    public async Task Report_RequeuePushedBeforeReportPush_KeptReportWrittenNextPassModelRanOnce()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model job = await SeedJobAsync(attempt: 1, ct);
        _hooks.BeforeRun = SweepBeforeReportPush(ct);
        BusListener listener = _node.GetRequiredService<BusListener>();
        (await listener.StartAsync(ct)).Should().Be(BusStartupResult.Clean);
        _hooks.BeforeRun = null;
        KeptFiles().Should().ContainSingle("the report commit lost the race and its result is kept");
        (await _harness.Bus.ExistsOnMainAsync(QueuedPath(job), ct)).Should().BeTrue("Central re-queued the job");

        // Act
        await listener.ProcessOnceAsync(ct);

        // Assert
        await AssertReportedOnceAsync(job, ct);
    }

    // AC-3: Central answers the claim (attempts exhausted) while the model runs; the node's report is committed alone.
    [Fact]
    public async Task Report_ExhaustedWhileRunning_ReportCommittedAlone()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model job = await SeedJobAsync(attempt: 3, ct);
        _hooks.BeforeRun = SweepAtModelStart(ct);
        BusListener listener = _node.GetRequiredService<BusListener>();

        // Act
        (await listener.StartAsync(ct)).Should().Be(BusStartupResult.Clean);

        // Assert
        string report = await AssertReportedOnceAsync(job, ct);
        string commit = (await _harness.Bus.RunGitAsync(_harness.Bus.BareDir, ["log", "-1", "--format=%H", "--fixed-strings", "--grep=acme report ", "--author=home-laptop", "main"], ct)).Trim();
        (await _harness.Bus.RunGitAsync(_harness.Bus.BareDir, ["show", "--name-only", "--format=", commit], ct)).Trim().Should().Be(report);
    }

    // AC-1: every push of the report write is refused; the result stays in the node folder, and the next pass puts it on the remote and clears it.
    [Fact]
    public async Task Report_PushRefused_KeptUntilNextPassPutsItOnRemote()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model job = await SeedJobAsync(attempt: 1, ct);
        bool refuse = false;
        _hooks.BeforeRun = spec =>
        {
            refuse |= IsReportCommit(spec);
            return Task.CompletedTask;
        };
        _hooks.Override = spec => refuse && HookedProcessRunner.SubCommand(spec) == "push" ? PushRefused : null;
        BusListener listener = _node.GetRequiredService<BusListener>();
        (await listener.StartAsync(ct)).Should().Be(BusStartupResult.Clean);
        KeptFiles().Should().ContainSingle("the report was committed but not pushed");
        (await ReportsOnMainAsync(ct)).Should().BeEmpty();
        _hooks.Override = null;
        _hooks.BeforeRun = null;

        // Act
        await listener.ProcessOnceAsync(ct);

        // Assert
        await AssertReportedOnceAsync(job, ct);
    }

    private static bool IsReportCommit(ProcessSpec spec) =>
        HookedProcessRunner.SubCommand(spec) == "commit" && (spec.StandardInput?.StartsWith("acme report ", StringComparison.Ordinal) ?? false);

    private static string QueuedPath(Model job) => BusPaths.EnvelopeFile(BusPaths.Jobs(CliHarness.Acme, CliHarness.HomeLaptop), job.Header.Id);

    private static string ClaimedPath(Model job) => BusPaths.EnvelopeFile(BusPaths.Claimed(CliHarness.Acme, CliHarness.HomeLaptop), job.Header.Id);

    // The node's one report answers the job, done; the job's file is gone from jobs/ and jobs/claimed/; the model ran once; nothing stays kept.
    private async Task<string> AssertReportedOnceAsync(Model job, CancellationToken ct)
    {
        string path = (await ReportsOnMainAsync(ct)).Should().ContainSingle().Subject;
        Model report = await _harness.ParseOnMainAsync(path, ct);
        report.Header.InReplyTo.Should().Be(job.Header.Id);
        report.Report!.Status.Should().Be(ReportStatus.Done);
        (await _harness.Bus.ExistsOnMainAsync(QueuedPath(job), ct)).Should().BeFalse();
        (await _harness.Bus.ExistsOnMainAsync(ClaimedPath(job), ct)).Should().BeFalse();
        _hooks.Calls.Count(c => c.FileName == FakeClaude.ExecutablePath).Should().Be(1, "the job is never run a second time");
        KeptFiles().Should().BeEmpty();
        return path;
    }

    private string[] KeptFiles()
    {
        string folder = KeptReports.DirectoryFor(NodeDir, CliHarness.Acme);
        return Directory.Exists(folder) ? Directory.GetFiles(folder, "*.md") : [];
    }

    // Central sweeps once, right when the node starts the model.
    private Func<ProcessSpec, Task> SweepAtModelStart(CancellationToken ct)
    {
        bool swept = false;
        return async spec =>
        {
            if (!swept && spec.FileName == FakeClaude.ExecutablePath)
            {
                swept = true;
                (await SweepAsync(ct)).Exit.Should().Be(0);
            }
        };
    }

    // Central sweeps once, after the node committed its report and before that commit's push.
    private Func<ProcessSpec, Task> SweepBeforeReportPush(CancellationToken ct)
    {
        bool committed = false;
        bool swept = false;
        return async spec =>
        {
            committed |= IsReportCommit(spec);
            if (committed && !swept && HookedProcessRunner.SubCommand(spec) == "push")
            {
                swept = true;
                (await SweepAsync(ct)).Exit.Should().Be(0);
            }
        };
    }

    // A queued job central → home-laptop for the scratch project, with a 2-minute timeout and the given attempt.
    private async Task<Model> SeedJobAsync(int attempt, CancellationToken ct)
    {
        Model job = await TestEnvelopes.SignedJobAsync(
            CliHarness.Central,
            CliHarness.HomeLaptop,
            _harness.Store,
            TestKeys.Acme1,
            "Read README.md and finish with a REPORT section; change nothing.\n",
            ct,
            fields: new JobFields(_project.Path, TimeoutMinutes: 2, AllowedTools: ["Read"], Attempt: attempt));
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
