using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Health;
using Zyggy.Core.Jobs;
using Zyggy.Core.Registry;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Cli;
using Zyggy.Integration.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Bus;

/// <summary>
/// Spec 61 slice 5 (AC-15 to AC-19), the slice's review check: <c>zyggy withdraw &lt;id&gt;</c> then <c>zyggy status --id &lt;id&gt;</c> against
/// a local bare repository. Central's <c>zyggy</c> runs in process over its own checkout; the home laptop is the production node composition
/// (the listener with the job runner and <c>tools/fake-claude</c>) over a second checkout of the same bare repository; real git throughout.
/// </summary>
public sealed class WithdrawRoundTripTests : IAsyncLifetime
{
    private const string Cart = "feature/12-cart";

    private readonly HookedProcessRunner _hooks = TestHost.Hooks();

    private readonly RecordingLoggerProvider _log = new();

    private CliHarness _harness = null!;

    private WorktreeProject _project = null!;

    private int _closedPort;

    private BusRepoFixture Bus => _harness.Bus;

    private Dictionary<string, string?> ClosedHealth => new() { ["ZYGGY_HEALTH_URL"] = $"http://127.0.0.1:{_closedPort}/health" };

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        _harness = await CliHarness.CreateAsync(ct);
        _project = await WorktreeProject.CreateAsync(Bus, ct);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _closedPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
    }

    public async ValueTask DisposeAsync()
    {
        _project?.Dispose();
        await _harness.DisposeAsync();
    }

    // AC-15: a queued job is withdrawn by its sender in one commit — the job file gone, the sender's failed / withdrawn report verifies —
    // and the node never runs it.
    [Fact]
    public async Task Withdraw_QueuedJob_OneCommitJobGoneSendersReportVerifiesNeverRun()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        SeededJob job = await SeedJobAsync(claimed: false, ct);
        int before = await _harness.CommitCountOnMainAsync(ct);

        // Act
        var (exit, console) = await WithdrawAsync(job, _harness.CentralNodeJson, ct, "--why", "not needed any more");
        await using ServiceProvider node = await StartNodeAsync(Long(), ct);

        // Assert
        string reportPath = (await ReportsOnMainAsync(CliHarness.Central, ct)).Should().ContainSingle().Subject;
        Model report = await _harness.ParseOnMainAsync(reportPath, ct);
        new
        {
            Exit = exit,
            console.Stdout,
            Commits = await _harness.CommitCountOnMainAsync(ct) - before,
            Subject = await LastSubjectAsync(ct),
            Queued = await Bus.ExistsOnMainAsync(job.RelativePath, ct),
            report.Header.From,
            report.Header.To,
            report.Header.InReplyTo,
            report.Report!.Status,
            report.Report.Reason,
            report.BodyText,
            ModelStarts = ModelStarts(),
        }.Should().BeEquivalentTo(
            new
            {
                Exit = 0,
                Stdout = "withdrawn " + job.Id.Value + " (was queued on home-laptop)\n",
                Commits = 1,
                Subject = "acme withdraw " + job.Id.Value + " central→home-laptop",
                Queued = false,
                From = CliHarness.Central,
                To = CliHarness.Central,
                InReplyTo = job.Id,
                Status = ReportStatus.Failed,
                Reason = "withdrawn",
                BodyText = "withdrawn before it started\nnot needed any more\n",
                ModelStarts = 0,
            },
            console.Stderr);
    }

    // AC-16: a running worktree job: the node stops the model, reports failed / withdrawn, removes claim and withdrawal in the report's own
    // commit, and keeps the work on origin's zyggy/<id>; the named branch on the origin is unchanged.
    [Fact]
    public async Task Withdraw_RunningWorktreeJob_ModelStoppedReportWithdrawnClaimAndWithdrawalGoneWorkKept()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await _project.GitAsync(["push", "-q", "origin", "main:refs/heads/" + Cart], ct);
        string? named = await _project.ResolveAsync(_project.OriginDir, "refs/heads/" + Cart, ct);
        await SeedRegistryAsync("0.30.1", ct);
        SeededJob job = await SeedJobAsync(claimed: false, ct, worktree: true);
        await using ServiceProvider node = BuildNode(Long());
        BusListener listener = node.GetRequiredService<BusListener>();
        Task<(int Exit, VerbConsole Console)>? withdrawn = null;
        _hooks.BeforeRun = spec =>
        {
            if (spec.FileName == FakeClaude.ExecutablePath && withdrawn is null)
            {
                // While the model runs: the owner withdraws on Central, the node's next pass sees the withdrawal.
                withdrawn = Task.Run(() => WithdrawWhileRunningAsync(node, listener, job, ct), ct);
            }

            return Task.CompletedTask;
        };
        var clock = Stopwatch.StartNew();

        // Act
        (await listener.StartAsync(ct)).Should().Be(BusStartupResult.Clean);

        // Assert
        clock.Stop();
        var (exit, console) = await withdrawn!;
        string reportPath = (await ReportsOnMainAsync(CliHarness.HomeLaptop, ct)).Should().ContainSingle().Subject;
        Model report = await _harness.ParseOnMainAsync(reportPath, ct);
        string reportCommit = await Bus.RunGitAsync(Bus.BareDir, ["log", "-1", "--format=%H", "main", "--", reportPath], ct);
        string changed = await Bus.RunGitAsync(Bus.BareDir, ["show", "--name-only", "--format=", reportCommit], ct);
        new
        {
            Exit = exit,
            console.Stdout,
            report.Report!.Status,
            report.Report.Reason,
            ByLine = report.BodyText.StartsWith("withdrawn by central at ", StringComparison.Ordinal),
            Claimed = await Bus.ExistsOnMainAsync(ClaimedPath(job.Id), ct),
            Withdrawals = (await WithdrawalsOnMainAsync(ct)).Count,
            CommitRemovesClaimAndWithdrawal = changed.Contains(ClaimedPath(job.Id), StringComparison.Ordinal) && changed.Contains(BusPaths.Withdrawals(CliHarness.Acme, CliHarness.HomeLaptop) + "/", StringComparison.Ordinal),
            Work = await _project.OriginGitAsync(["show", "refs/heads/zyggy/" + job.Id.Value + ":src/cart.txt"], ct),
            Named = await _project.ResolveAsync(_project.OriginDir, "refs/heads/" + Cart, ct),
            StoppedEarly = clock.Elapsed < TimeSpan.FromSeconds(100),
            ModelStarts = ModelStarts(),
        }.Should().BeEquivalentTo(
            new
            {
                Exit = 0,
                Stdout = "withdrawal requested " + job.Id.Value + " (claimed by home-laptop)\n",
                Status = ReportStatus.Failed,
                Reason = "withdrawn",
                ByLine = true,
                Claimed = false,
                Withdrawals = 0,
                CommitRemovesClaimAndWithdrawal = true,
                Work = "half done",
                Named = named,
                StoppedEarly = true,
                ModelStarts = 1,
            },
            report.BodyText);
    }

    // AC-17: a withdrawal for a job that was reported meanwhile is removed as "nothing to stop", and no second report is written.
    [Fact]
    public async Task NodePass_WithdrawalForReportedJob_RemovedNothingToStopNoSecondReport()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        SeededJob job = await SeedJobAsync(claimed: false, ct);
        await using ServiceProvider node = await StartNodeAsync(Quick(), ct);
        string withdrawal = await SeedWithdrawalAsync(CliHarness.Central, job.Id, ct);
        BusListener listener = node.GetRequiredService<BusListener>();
        (await node.GetRequiredService<BusRepository>().PullAsync(ct)).Succeeded.Should().BeTrue();

        // Act
        await listener.ProcessOnceAsync(ct);

        // Assert
        new
        {
            Withdrawal = await Bus.ExistsOnMainAsync(withdrawal, ct),
            Subject = await LastSubjectAsync(ct),
            Reports = (await ReportsOnMainAsync(CliHarness.HomeLaptop, ct)).Count,
        }.Should().BeEquivalentTo(new
        {
            Withdrawal = false,
            Subject = "acme withdrawal " + job.Id.Value + " home-laptop→central nothing to stop",
            Reports = 1,
        });
    }

    // AC-17: a withdrawal from a machine that is neither the job's sender nor Central is left in place, the job is not touched, and the
    // node says so once over two passes.
    [Fact]
    public async Task NodePass_WithdrawalFromAnotherMachine_LeftInPlaceLoggedOnce()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        SeededJob job = await SeedJobAsync(claimed: true, ct);
        string withdrawal = await SeedWithdrawalAsync(CliHarness.WorkLaptop, job.Id, ct);
        await using ServiceProvider node = BuildNode(Quick());
        BusListener listener = node.GetRequiredService<BusListener>();
        (await listener.StartAsync(ct)).Should().Be(BusStartupResult.Clean);

        // Act: a second pass of the same listener
        await listener.ProcessOnceAsync(ct);

        // Assert
        new
        {
            Withdrawal = await Bus.ExistsOnMainAsync(withdrawal, ct),
            Claimed = await Bus.ExistsOnMainAsync(ClaimedPath(job.Id), ct),
            Reports = (await ReportsOnMainAsync(CliHarness.HomeLaptop, ct)).Count,
            Warnings = _log.AtLevel(LogLevel.Warning).Count(e => e.Message.Contains("withdrawal", StringComparison.Ordinal) && e.Message.Contains("not accepted", StringComparison.Ordinal)),
        }.Should().BeEquivalentTo(new { Withdrawal = true, Claimed = true, Reports = 0, Warnings = 1 });
    }

    // AC-18: an id nothing on the bus names: exit 1, nothing written.
    [Fact]
    public async Task Withdraw_UnknownId_ExitOneNothingWritten()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var unknown = EnvelopeId.New(DateTimeOffset.UtcNow);
        int before = await _harness.CommitCountOnMainAsync(ct);

        // Act
        var (exit, console) = await _harness.RunAsync(["withdraw", unknown.Value], _harness.CentralNodeJson, ct: ct);

        // Assert
        new { Exit = exit, console.Stdout, Commits = await _harness.CommitCountOnMainAsync(ct) - before }
            .Should().BeEquivalentTo(new { Exit = 1, Stdout = unknown.Value + ": not found\n", Commits = 0 }, console.Stderr);
    }

    // AC-18: a job already reported: exit 1, nothing written.
    [Fact]
    public async Task Withdraw_ReportedJob_ExitOneNothingWritten()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        SeededJob job = await SeedJobAsync(claimed: false, ct);
        await Bus.RunGitAsync(Bus.SenderDir, ["rm", "-q", job.RelativePath], ct);
        await Bus.RunGitAsync(Bus.SenderDir, ["commit", "-q", "-m", "acme claim " + job.Id.Value], ct);
        await Bus.RunGitAsync(Bus.SenderDir, ["push", "-q", "origin", "main"], ct);
        await Bus.SeedSignedReportAsync(CliHarness.HomeLaptop, CliHarness.Central, job.Id, TestKeys.Acme1, _harness.Store, "done\n", ct: ct);
        int before = await _harness.CommitCountOnMainAsync(ct);

        // Act
        var (exit, console) = await WithdrawAsync(job, _harness.CentralNodeJson, ct);

        // Assert
        new { Exit = exit, console.Stdout, Commits = await _harness.CommitCountOnMainAsync(ct) - before }
            .Should().BeEquivalentTo(new { Exit = 1, Stdout = job.Id.Value + ": already reported (done)\n", Commits = 0 }, console.Stderr);
    }

    // AC-18: on a laptop, a job another machine sent is refused with exit 5 and nothing is written.
    [Fact]
    public async Task Withdraw_AnotherSendersJobOnALaptop_ExitFiveNothingWritten()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        SeededJob job = await SeedJobAsync(claimed: false, ct);
        int before = await _harness.CommitCountOnMainAsync(ct);

        // Act
        var (exit, console) = await WithdrawAsync(job, _harness.HomeLaptopNodeJson, ct);

        // Assert
        new { Exit = exit, console.Stderr, Commits = await _harness.CommitCountOnMainAsync(ct) - before, Queued = await Bus.ExistsOnMainAsync(job.RelativePath, ct) }
            .Should().BeEquivalentTo(new { Exit = 5, Stderr = "zyggy: " + job.Id.Value + ": submitted by central; withdraw it there or on Central\n", Commits = 0, Queued = true });
    }

    // AC-18: a claimed session task is not withdrawn: exit 5, nothing written.
    [Fact]
    public async Task Withdraw_ClaimedSessionTask_ExitFiveNothingWritten()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        SeededJob job = await SeedJobAsync(claimed: true, ct, session: true);
        int before = await _harness.CommitCountOnMainAsync(ct);

        // Act
        var (exit, console) = await WithdrawAsync(job, _harness.CentralNodeJson, ct);

        // Assert
        new { Exit = exit, console.Stderr, Commits = await _harness.CommitCountOnMainAsync(ct) - before }
            .Should().BeEquivalentTo(new { Exit = 5, Stderr = "zyggy: " + job.Id.Value + ": a session task; finish it with zyggy work --failed\n", Commits = 0 });
    }

    // Task: the target runs 0.30.0, which does not stop a running job; the verb says so on a second line.
    [Fact]
    public async Task Withdraw_ClaimedJobOnA0300Node_SecondLineSaysTheRunIsNotStopped()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await SeedRegistryAsync("0.30.0", ct);
        SeededJob job = await SeedJobAsync(claimed: true, ct);

        // Act
        var (exit, console) = await WithdrawAsync(job, _harness.CentralNodeJson, ct);

        // Assert
        new { Exit = exit, console.Stdout }.Should().BeEquivalentTo(
            new
            {
                Exit = 0,
                Stdout = "withdrawal requested " + job.Id.Value + " (claimed by home-laptop)\nhome-laptop runs 0.30.0: the run is not stopped before 0.30.1\n",
            },
            console.Stderr);
    }

    // AC-19: while the withdrawal waits, `zyggy status --id` names it.
    [Fact]
    public async Task StatusId_WithdrawalPending_ShowsRequestedLine()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        SeededJob job = await SeedJobAsync(claimed: true, ct);
        (await WithdrawAsync(job, _harness.CentralNodeJson, ct)).Exit.Should().Be(0);

        // Act
        var (exit, console) = await _harness.RunAsync(["status", "--id", job.Id.Value], _harness.CentralNodeJson, extraEnvironment: ClosedHealth, ct: ct);

        // Assert
        new { Exit = exit, Line = console.Stdout.Split('\n').Any(l => l.StartsWith("withdrawal requested ", StringComparison.Ordinal) && l.EndsWith(" by central, waiting for home-laptop", StringComparison.Ordinal)) }
            .Should().BeEquivalentTo(new { Exit = 0, Line = true }, console.Stdout);
    }

    // AC-19: `zyggy work` shows the claimed job with its pending withdrawal.
    [Fact]
    public async Task Work_WithdrawalPending_JobLineReadsClaimedWithdrawalRequested()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        SeededJob job = await SeedJobAsync(claimed: true, ct);
        (await WithdrawAsync(job, _harness.CentralNodeJson, ct)).Exit.Should().Be(0);

        // Act
        var (exit, console) = await _harness.RunAsync(["work"], _harness.CentralNodeJson, ct: ct);

        // Assert
        new { Exit = exit, Line = console.Stdout.Split('\n').Any(l => l.Contains("job " + job.Id.Value, StringComparison.Ordinal) && l.Contains("  claimed, withdrawal requested  ", StringComparison.Ordinal)) }
            .Should().BeEquivalentTo(new { Exit = 0, Line = true }, console.Stdout);
    }

    // AC-19: the overview's bus block counts the pending withdrawals.
    [Fact]
    public async Task Status_WithdrawalPending_BusBlockCountsIt()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        SeededJob job = await SeedJobAsync(claimed: true, ct);
        (await WithdrawAsync(job, _harness.CentralNodeJson, ct)).Exit.Should().Be(0);

        // Act
        var (_, console) = await _harness.RunAsync(["status"], _harness.CentralNodeJson, extraEnvironment: ClosedHealth, ct: ct);

        // Assert
        console.Stdout.Split('\n').Should().Contain("        withdrawals pending 1", console.Stdout);
    }

    // AC-19 and the review check: after the node acted on the withdrawal, `zyggy status --id` shows the job as withdrawn.
    [Fact]
    public async Task StatusId_AfterTheNodeActed_ShowsWithdrawn()
    {
        // Arrange: a claimed job the node is not running (resumed claims are not started here)
        var ct = TestContext.Current.CancellationToken;
        SeededJob job = await SeedJobAsync(claimed: true, ct);
        (await WithdrawAsync(job, _harness.CentralNodeJson, ct)).Exit.Should().Be(0);
        await using ServiceProvider node = await StartNodeAsync(Quick(), ct);
        await Bus.RunGitAsync(Bus.SenderDir, ["pull", "-q", "--rebase", "origin", "main"], ct);

        // Act
        var (exit, console) = await _harness.RunAsync(["status", "--id", job.Id.Value], _harness.CentralNodeJson, extraEnvironment: ClosedHealth, ct: ct);

        // Assert
        string[] lines = console.Stdout.Split('\n');
        new
        {
            Exit = exit,
            State = lines[1],
            Report = lines.Any(l => l.StartsWith("report  ", StringComparison.Ordinal) && l.Contains("  failed withdrawn", StringComparison.Ordinal)),
            Pending = lines.Any(l => l.StartsWith("withdrawal requested", StringComparison.Ordinal)),
            Claimed = await Bus.ExistsOnMainAsync(ClaimedPath(job.Id), ct),
            ModelStarts = ModelStarts(),
        }.Should().BeEquivalentTo(new { Exit = 0, State = "state   withdrawn, reported by home-laptop", Report = true, Pending = false, Claimed = false, ModelStarts = 0 }, console.Stdout);
    }

    private static string ClaimedPath(EnvelopeId id) => BusPaths.EnvelopeFile(BusPaths.Claimed(CliHarness.Acme, CliHarness.HomeLaptop), id);

    // A model that writes a file in its working directory and then runs for two minutes, unless stopped.
    private static Dictionary<string, string> Long() => new()
    {
        ["ZYGGY_FAKE_CLAUDE_SCENARIO"] = "done",
        ["ZYGGY_FAKE_CLAUDE_EDITS"] = "+src/cart.txt\thalf done",
        ["ZYGGY_FAKE_CLAUDE_DELAY_MS"] = "120000",
    };

    private static Dictionary<string, string> Quick() => new() { ["ZYGGY_FAKE_CLAUDE_SCENARIO"] = "done" };

    private int ModelStarts() => _hooks.Calls.Count(c => c.FileName == FakeClaude.ExecutablePath);

    private Task<(int Exit, VerbConsole Console)> WithdrawAsync(SeededJob job, string nodeJson, CancellationToken ct, params string[] more) =>
        _harness.RunAsync(["withdraw", job.Id.Value, .. more], nodeJson, ct: ct);

    // Waits until the model wrote its file in the job's worktree, withdraws on Central, and lets the node's next pass see it.
    private async Task<(int Exit, VerbConsole Console)> WithdrawWhileRunningAsync(ServiceProvider node, BusListener listener, SeededJob job, CancellationToken ct)
    {
        string written = Path.Combine(TranscriptPaths.WorktreeFor(_project.NodeDir, CliHarness.Acme, job.Id), "src", "cart.txt");
        var waited = Stopwatch.StartNew();
        while (!File.Exists(written) && waited.Elapsed < TimeSpan.FromSeconds(60))
        {
            await Task.Delay(100, ct);
        }

        var result = await WithdrawAsync(job, _harness.CentralNodeJson, ct);
        (await node.GetRequiredService<BusRepository>().PullAsync(ct)).Succeeded.Should().BeTrue();
        await listener.ProcessOnceAsync(ct);
        return result;
    }

    private ServiceProvider BuildNode(Dictionary<string, string> environment) =>
        TestHost.BuildRunnerListener(
            Bus,
            CliHarness.HomeLaptop,
            _harness.Store,
            FakeClaude.ExecutablePath,
            TestHost.NodeConfigurationFor(Bus, CliHarness.HomeLaptop),
            o =>
            {
                o.NodeDir = _project.NodeDir;
                o.TimeoutOverride = TimeSpan.FromMinutes(10);
                o.ModelEnvironment = environment;
            },
            hooks: _hooks,
            logging: _log);

    private async Task<ServiceProvider> StartNodeAsync(Dictionary<string, string> environment, CancellationToken ct)
    {
        ServiceProvider node = BuildNode(environment);
        (await node.GetRequiredService<BusListener>().StartAsync(ct)).Should().Be(BusStartupResult.Clean);
        return node;
    }

    // A job central → home-laptop for the worktree project: queued in jobs/, or already claimed by the home laptop.
    private async Task<SeededJob> SeedJobAsync(bool claimed, CancellationToken ct, bool worktree = false, bool session = false)
    {
        var fields = new JobFields(
            _project.Path,
            Worktree: worktree,
            TimeoutMinutes: 30,
            AllowedTools: ["Read", "Edit"],
            Branch: worktree ? Cart : null,
            Deliver: session ? JobDelivery.Session : JobDelivery.Run);
        SeededJob job = await Bus.SeedSignedJobAsync(CliHarness.Central, CliHarness.HomeLaptop, TestKeys.Acme1, _harness.Store, "Change the shop.\n", fields: fields, ct: ct);
        if (!claimed)
        {
            return job;
        }

        // The home laptop's claim, as its listener writes it: one git mv into jobs/claimed/.
        string to = ClaimedPath(job.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(Bus.SenderDir, to))!);
        await Bus.RunGitAsync(Bus.SenderDir, ["mv", job.RelativePath, to], ct);
        await Bus.RunGitAsync(Bus.SenderDir, ["commit", "-q", "-m", "acme claim " + job.Id.Value + " central→home-laptop"], ct);
        await Bus.RunGitAsync(Bus.SenderDir, ["push", "-q", "origin", "main"], ct);
        return job with { RelativePath = to };
    }

    private async Task<string> SeedWithdrawalAsync(MachineName from, EnvelopeId job, CancellationToken ct)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var header = new EnvelopeHeader(CliHarness.Acme, EnvelopeId.New(now), from, CliHarness.HomeLaptop, now, InReplyTo: job);
        EnvelopeResult signed = await new EnvelopeSigner(_harness.Store).SignAsync(Model.CreateWithdrawal(header, why: null), TestKeys.Acme1, ct);
        signed.IsAccepted.Should().BeTrue(signed.Rejection?.ToString());
        string path = BusPaths.EnvelopeFile(BusPaths.Withdrawals(CliHarness.Acme, CliHarness.HomeLaptop), header.Id);
        await Bus.SeedRawFileAsync(path, EnvelopeWriter.Serialize(signed.Envelope!), "acme withdrawal " + job.Value + " " + from.Value + "→home-laptop", ct);
        return path;
    }

    private async Task SeedRegistryAsync(string version, CancellationToken ct)
    {
        var document = new RegistryDocument(CliHarness.Acme, CliHarness.HomeLaptop, RegistryOs.Windows, DateTimeOffset.UtcNow, HealthLevel.Green, null, []) { Version = version };
        await Bus.SeedRawFileAsync(BusPaths.Registry(CliHarness.Acme, CliHarness.HomeLaptop), document.Serialize(), "acme registry home-laptop", ct);
    }

    private Task<string> LastSubjectAsync(CancellationToken ct) => Bus.RunGitAsync(Bus.BareDir, ["log", "-1", "--format=%s", "main"], ct);

    private async Task<IReadOnlyList<string>> ReportsOnMainAsync(MachineName machine, CancellationToken ct)
    {
        string output = await Bus.RunGitAsync(Bus.BareDir, ["ls-tree", "--name-only", "main", BusPaths.Reports(CliHarness.Acme, machine) + "/"], ct);
        return [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(p => p.EndsWith(".md", StringComparison.Ordinal))];
    }

    private async Task<IReadOnlyList<string>> WithdrawalsOnMainAsync(CancellationToken ct)
    {
        string output = await Bus.RunGitAsync(Bus.BareDir, ["ls-tree", "--name-only", "main", BusPaths.Withdrawals(CliHarness.Acme, CliHarness.HomeLaptop) + "/"], ct);
        return [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(p => p.EndsWith(".md", StringComparison.Ordinal))];
    }
}
