using System.Text;

using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Cli;

/// <summary>
/// Spec 17 slice 2 (AC-2 wire, AC-7, AC-8, AC-14): <c>zyggy sweep</c> in process on Central's checkout of a bare repository re-queues a
/// stale claim with <c>attempt: 2</c>, writes nothing on a second pass, refuses a machine that is not Central and a busy checkout, and
/// the real listener on the home laptop then claims, runs (fake <c>claude</c>) and reports the re-queued job.
/// </summary>
public sealed class SweepRequeueTests : IAsyncLifetime
{
    /// <summary>How far ahead the verb's clock runs: a claim committed just now is past its 2-minute timeout + 15 min.</summary>
    private static readonly TimeSpan Ahead = TimeSpan.FromHours(1);

    private CliHarness _harness = null!;

    private ScratchProject _project = null!;

    public async ValueTask InitializeAsync()
    {
        _harness = await CliHarness.CreateAsync(CancellationToken.None);
        _project = await ScratchProject.CreateAsync(_harness.Bus, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        _project?.Dispose();
        await _harness.DisposeAsync();
    }

    // AC-2 (wire): paths, fields, signature and commit subject on the remote.
    [Fact]
    public async Task Sweep_StaleClaim_RequeuedWithAttemptTwoOnRemote()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model job = await SeedClaimAsync(ct);

        // Act
        var (exit, console) = await SweepAsync(_harness.CentralNodeJson, new OffsetClock(Ahead), ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        console.Stdout.Should().Be($"requeued {job.Header.Id.Value} home-laptop attempt 2\nsweep: 1 re-queued, 0 exhausted, 0 rejected, 0 archived\n");
        Model requeued = await _harness.ParseOnMainAsync(BusPaths.EnvelopeFile(BusPaths.Jobs(CliHarness.Acme, CliHarness.HomeLaptop), job.Header.Id), ct);
        requeued.Job!.Attempt.Should().Be(2);
        requeued.KeyId.Should().Be(TestKeys.Acme1);
        Encoding.UTF8.GetString(EnvelopeSigner.Canonicalize(requeued.WithAttempt(1))).Should().Be(Encoding.UTF8.GetString(EnvelopeSigner.Canonicalize(job.WithAttempt(1))), "only attempt (and the signature) changes; every other field and the body are the job's");
        (await _harness.Bus.ExistsOnMainAsync(ClaimedPath(job), ct)).Should().BeFalse();
        (await _harness.Bus.LastCommitSubjectAsync(ct)).Trim().Should().Be($"acme requeue {job.Header.Id.Value} central→home-laptop");
    }

    // AC-7: a second pass over the result of the first writes no commit.
    [Fact]
    public async Task Sweep_SecondPass_WritesNoCommit()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await SeedClaimAsync(ct);
        (await SweepAsync(_harness.CentralNodeJson, new OffsetClock(Ahead), ct)).Exit.Should().Be(0);
        int commits = await _harness.CommitCountOnMainAsync(ct);

        // Act
        var (exit, console) = await SweepAsync(_harness.CentralNodeJson, new OffsetClock(Ahead), ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        console.Stdout.Should().Be("sweep: 0 re-queued, 0 exhausted, 0 rejected, 0 archived\n");
        (await _harness.CommitCountOnMainAsync(ct)).Should().Be(commits);
    }

    // AC-8: the node claims the re-queued job (attempt 2 accepted by intake), runs it and reports done.
    [Fact]
    public async Task Sweep_RequeuedJob_NodeClaimsRunsReportsDone()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model job = await SeedClaimAsync(ct);
        (await SweepAsync(_harness.CentralNodeJson, new OffsetClock(Ahead), ct)).Exit.Should().Be(0);
        await _harness.Bus.PullCloneAsync(ct);
        await using ServiceProvider services = TestHost.BuildRunnerListener(
            _harness.Bus,
            CliHarness.HomeLaptop,
            await TestHost.StoreAsync(CliHarness.Acme, ct),
            FakeClaude.ExecutablePath,
            TestHost.NodeConfigurationFor(_harness.Bus, CliHarness.HomeLaptop),
            o =>
            {
                o.NodeDir = _harness.NodeDirOf("home-laptop-listener");
                o.TimeoutOverride = TimeSpan.FromSeconds(30);
                o.ModelEnvironment = new Dictionary<string, string> { ["ZYGGY_FAKE_CLAUDE_SCENARIO"] = "done" };
            },
            withDispatcher: true);
        BusListener listener = services.GetRequiredService<BusListener>();
        (await listener.StartAsync(ct)).Should().Be(BusStartupResult.Clean);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Act
        Task run = listener.RunAsync(PollMode.Active, cts.Token);
        IReadOnlyList<string> reports = await WaitForReportsAsync(ct);
        await cts.CancelAsync();
        await run;
        await listener.DrainAsync(ct);

        // Assert
        Model report = await _harness.ParseOnMainAsync(reports.Should().ContainSingle().Subject, ct);
        report.Header.InReplyTo.Should().Be(job.Header.Id);
        report.Report!.Status.Should().Be(ReportStatus.Done);
        (await _harness.Bus.ExistsOnMainAsync(BusPaths.EnvelopeFile(BusPaths.Jobs(CliHarness.Acme, CliHarness.HomeLaptop), job.Header.Id), ct)).Should().BeFalse();
        (await _harness.Bus.ExistsOnMainAsync(ClaimedPath(job), ct)).Should().BeFalse();
        (await ClaimedAttemptAsync(job, ct)).Should().Be(2, "intake accepted the re-queued job at attempt 2 and claimed it as is");
    }

    // AC-14: only Central sweeps.
    [Fact]
    public async Task Sweep_RoleNode_ExitsThreeWritesNothing()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model job = await SeedClaimAsync(ct);
        int commits = await _harness.CommitCountOnMainAsync(ct);

        // Act
        var (exit, console) = await SweepAsync(_harness.HomeLaptopNodeJson, new OffsetClock(Ahead), ct);

        // Assert
        exit.Should().Be(3);
        console.Stderr.Should().Be("sweep: only Central sweeps (role is node)\n");
        console.Stdout.Should().BeEmpty();
        (await _harness.CommitCountOnMainAsync(ct)).Should().Be(commits);
        (await _harness.Bus.ExistsOnMainAsync(ClaimedPath(job), ct)).Should().BeTrue();
    }

    // AC-14: a checkout lock busy for the whole wait.
    [Fact]
    public async Task Sweep_LockBusy_ExitsFour()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model job = await SeedClaimAsync(ct);
        int commits = await _harness.CommitCountOnMainAsync(ct);
        using IDisposable? held = await BusCheckoutLock.AcquireAsync(_harness.Bus.SenderDir, BusCheckoutLock.WriteLockName, TimeSpan.Zero, TimeProvider.System, ct);
        held.Should().NotBeNull();

        // Act
        var (exit, console) = await SweepAsync(_harness.CentralNodeJson, new JumpingClock(Ahead), ct);

        // Assert
        exit.Should().Be(4, console.Stderr);
        console.Stdout.Should().BeEmpty();
        (await _harness.CommitCountOnMainAsync(ct)).Should().Be(commits);
        (await _harness.Bus.ExistsOnMainAsync(ClaimedPath(job), ct)).Should().BeTrue();
    }

    private static string ClaimedPath(Model job) => BusPaths.EnvelopeFile(BusPaths.Claimed(CliHarness.Acme, CliHarness.HomeLaptop), job.Header.Id);

    // A job central → home-laptop sitting in home-laptop's jobs/claimed/, committed now; Central's checkout is the sender clone that pushed it.
    private async Task<Model> SeedClaimAsync(CancellationToken ct)
    {
        Model job = await TestEnvelopes.SignedJobAsync(
            CliHarness.Central,
            CliHarness.HomeLaptop,
            _harness.Store,
            TestKeys.Acme1,
            "Read README.md and finish with a REPORT section; change nothing.\n",
            ct,
            fields: new JobFields(_project.Path, TimeoutMinutes: 2, AllowedTools: ["Read"]));
        await _harness.Bus.SeedRawFileAsync(ClaimedPath(job), EnvelopeWriter.Serialize(job), "acme claim " + job.Header.Id.Value + " home-laptop", ct);
        return job;
    }

    // The attempt of the file the node's own claim commit (the newest `acme claim <id>` on main) put into jobs/claimed/.
    private async Task<int> ClaimedAttemptAsync(Model job, CancellationToken ct)
    {
        string claim = (await _harness.Bus.RunGitAsync(_harness.Bus.BareDir, ["log", "-1", "--format=%H", "--fixed-strings", "--grep=acme claim " + job.Header.Id.Value, "main"], ct)).Trim();
        byte[] bytes = await _harness.Bus.RunGitBytesAsync(_harness.Bus.BareDir, ["show", claim + ":" + ClaimedPath(job)], ct);
        EnvelopeResult parsed = await new EnvelopeSigner(_harness.Store).ParseAndVerifyAsync(bytes, CliHarness.Acme, ct);
        parsed.IsAccepted.Should().BeTrue(parsed.Rejection?.ToString());
        return parsed.Envelope!.Job!.Attempt;
    }

    private Task<(int Exit, VerbConsole Console)> SweepAsync(string nodeJson, TimeProvider clock, CancellationToken ct) =>
        _harness.RunAsync(["sweep"], nodeJson, more: s => s.AddSingleton(clock), ct: ct);

    private async Task<IReadOnlyList<string>> WaitForReportsAsync(CancellationToken ct)
    {
        List<string> reports = [];
        for (int i = 0; i < 120 && reports.Count == 0; i++)
        {
            await Task.Delay(250, ct);
            string output = await _harness.Bus.RunGitAsync(_harness.Bus.BareDir, ["ls-tree", "--name-only", "main", BusPaths.Reports(CliHarness.Acme, CliHarness.HomeLaptop) + "/"], ct);
            reports = [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(p => p.EndsWith(".md", StringComparison.Ordinal))];
        }

        return reports;
    }

    /// <summary>The system clock moved ahead by a fixed amount; timers stay real.</summary>
    private sealed class OffsetClock(TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + offset;
    }

    /// <summary>A clock that jumps 61 s on every read: a lock wait of 60 s ends after one poll without a real minute passing.</summary>
    private sealed class JumpingClock(TimeSpan offset) : TimeProvider
    {
        private long _reads;

        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + offset + TimeSpan.FromSeconds(61 * Interlocked.Increment(ref _reads));
    }
}
