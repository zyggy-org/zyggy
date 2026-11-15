using System.Text;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Cli;

/// <summary>
/// Spec 17 slice 4 (AC-9a, AC-9b, AC-9c): a node's report and Central's re-queue of the same claim, each committed before the other is
/// pushed, on two checkouts of one bare repository with real git. The commit that loses the race is dropped, the checkout ends on the bus
/// branch, clean, with no rebase in progress, and nothing is force-pushed.
/// </summary>
public sealed class SweepConflictTests : IAsyncLifetime
{
    /// <summary>How far ahead Central's clock runs: a claim committed just now is past its default 30-minute timeout + 15 min.</summary>
    private static readonly TimeSpan Ahead = TimeSpan.FromHours(1);

    private readonly HookedProcessRunner _nodeHooks = TestHost.Hooks();

    private readonly RecordingLoggerProvider _nodeLog = new();

    private CliHarness _harness = null!;

    private ServiceProvider _node = null!;

    public async ValueTask InitializeAsync()
    {
        _harness = await CliHarness.CreateAsync(CancellationToken.None);
        _node = TestHost.Build(_harness.Bus, CliHarness.HomeLaptop, _harness.Store, _nodeHooks, logging: _nodeLog);
    }

    public async ValueTask DisposeAsync()
    {
        await _node.DisposeAsync();
        await _harness.DisposeAsync();
    }

    // AC-9a: Central's re-queue is pushed first; the node's report commit loses and is dropped, its checkout ends clean.
    [Fact]
    public async Task Report_RequeuePushedFirst_ReportDroppedCheckoutClean()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (BusRepository repository, AcceptedJob job) = await ClaimedOnNodeAsync(ct);
        _nodeHooks.BeforeRun = SweepBeforeFirstPush(ct);

        // Act
        WriteOutcome outcome = await repository.ReportAsync(job, new ReportFields(ReportStatus.Done), Encoding.UTF8.GetBytes("ok\n"), TestKeys.Acme1, ct);

        // Assert
        string state = await CheckoutStateAsync(_harness.Bus.CloneDir, ct);
        outcome.Should().BeOfType<WriteFailed>(state);
        (await RebaseInProgressAsync(_harness.Bus.CloneDir, ct)).Should().BeFalse(state);
        (await _harness.Bus.RunGitAsync(_harness.Bus.CloneDir, ["rev-parse", "--abbrev-ref", "HEAD"], ct)).Trim().Should().Be("main", state);
        (await _harness.PorcelainAsync(_harness.Bus.CloneDir, ct)).Should().BeEmpty(state);
        (await _harness.Bus.RunGitAsync(_harness.Bus.CloneDir, ["rev-list", "--count", "origin/main..HEAD"], ct)).Trim().Should().Be("0", state);
        (await ReportsOnMainAsync(ct)).Should().BeEmpty();
        (await _harness.Bus.ExistsOnMainAsync(BusPaths.EnvelopeFile(BusPaths.Jobs(CliHarness.Acme, CliHarness.HomeLaptop), job.File.Id), ct)).Should().BeTrue();
        _nodeLog.AtLevel(LogLevel.Warning).Should().ContainSingle(m => m.Message.Contains("acme report ", StringComparison.Ordinal));
    }

    // AC-9a (second half): after the recovery the node sees the re-queued job at attempt 2, accepts it and claims it again.
    [Fact]
    public async Task Report_RequeuePushedFirst_NodeClaimsRequeuedJob()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (BusRepository repository, AcceptedJob job) = await ClaimedOnNodeAsync(ct);
        _nodeHooks.BeforeRun = SweepBeforeFirstPush(ct);
        (await repository.ReportAsync(job, new ReportFields(ReportStatus.Done), Encoding.UTF8.GetBytes("ok\n"), TestKeys.Acme1, ct)).Should().BeOfType<WriteFailed>();
        _nodeHooks.BeforeRun = null;
        (await repository.PullAsync(ct)).Succeeded.Should().BeTrue();
        var again = (AcceptedJob)await repository.VerifyIntakeAsync((await repository.ListNewJobsAsync(ct)).Single(), ct);

        // Act
        WriteOutcome claim = await repository.ClaimAsync(again, ct);

        // Assert
        claim.Should().BeOfType<Pushed>();
        again.File.Id.Should().Be(job.File.Id);
        again.Job.Job!.Attempt.Should().Be(2);
        (await _harness.Bus.ExistsOnMainAsync(ClaimedPath(job), ct)).Should().BeTrue();
    }

    // AC-9b: the node's report is pushed first; Central's re-queue commit loses and is dropped, its checkout ends clean.
    [Fact]
    public async Task Sweep_ReportPushedFirst_RequeueDroppedNextPassEmpty()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (BusRepository repository, AcceptedJob job) = await ClaimedOnNodeAsync(ct);
        bool reported = false;
        _harness.Hooks.BeforeRun = async spec =>
        {
            if (!reported && HookedProcessRunner.SubCommand(spec) == "push")
            {
                reported = true;
                (await repository.ReportAsync(job, new ReportFields(ReportStatus.Done), Encoding.UTF8.GetBytes("ok\n"), TestKeys.Acme1, ct)).Should().BeOfType<Pushed>();
            }
        };

        // Act
        var (exit, console) = await SweepAsync(ct);

        // Assert
        string state = await CheckoutStateAsync(_harness.Bus.SenderDir, ct);
        exit.Should().Be(6, console.Stderr + "\n" + state);
        (await RebaseInProgressAsync(_harness.Bus.SenderDir, ct)).Should().BeFalse(state);
        (await _harness.Bus.RunGitAsync(_harness.Bus.SenderDir, ["rev-parse", "--abbrev-ref", "HEAD"], ct)).Trim().Should().Be("main", state);
        (await _harness.PorcelainAsync(_harness.Bus.SenderDir, ct)).Should().BeEmpty(state);
        (await _harness.Bus.RunGitAsync(_harness.Bus.SenderDir, ["rev-list", "--count", "origin/main..HEAD"], ct)).Trim().Should().Be("0", state);
        (await ReportsOnMainAsync(ct)).Should().ContainSingle();
        (await _harness.Bus.ExistsOnMainAsync(BusPaths.EnvelopeFile(BusPaths.Jobs(CliHarness.Acme, CliHarness.HomeLaptop), job.File.Id), ct)).Should().BeFalse();
        _harness.Hooks.BeforeRun = null;
        int commits = await _harness.CommitCountOnMainAsync(ct);
        (await SweepAsync(ct)).Exit.Should().Be(0);
        (await _harness.CommitCountOnMainAsync(ct)).Should().Be(commits, "the next pass finds no claim");
    }

    // AC-9c: an earlier unpushed commit on another path survives the recovery and goes out with the next write.
    [Fact]
    public async Task Report_EarlierDeferredCommit_SurvivesAndPushesWithNextWrite()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (BusRepository repository, AcceptedJob job) = await ClaimedOnNodeAsync(ct);
        _nodeHooks.Override = spec => HookedProcessRunner.SubCommand(spec) == "push"
            ? new Zyggy.Core.Processes.ProcessResult(1, string.Empty, "! [rejected] main -> main (fetch first)", false, false, false, TimeSpan.Zero)
            : null;
        Model context = await ContextAsync(ct);
        (await repository.PublishContextAsync(context, ct)).Should().BeOfType<Deferred>();
        _nodeHooks.Override = null;
        _nodeHooks.BeforeRun = SweepBeforeFirstPush(ct);
        (await repository.ReportAsync(job, new ReportFields(ReportStatus.Done), Encoding.UTF8.GetBytes("ok\n"), TestKeys.Acme1, ct)).Should().BeOfType<WriteFailed>();
        string unpushed = (await _harness.Bus.RunGitAsync(_harness.Bus.CloneDir, ["log", "--format=%s", "origin/main..HEAD"], ct)).Trim();
        _nodeHooks.BeforeRun = null;

        // Act
        WriteOutcome next = await repository.PublishContextAsync(await ContextAsync(ct), ct);

        // Assert
        unpushed.Should().Be($"acme context {context.Header.Id.Value} home-laptop→central", "the deferred commit is the only one left unpushed after the recovery");
        next.Should().BeOfType<Pushed>();
        (await _harness.Bus.ExistsOnMainAsync(BusPaths.EnvelopeFile(BusPaths.Context(CliHarness.Acme, CliHarness.HomeLaptop), context.Header.Id), ct)).Should().BeTrue();
    }

    // Owner decision 2026-11-15: a report deferred while every push failed, then re-queued by Central, stops the next write's first pull;
    // that report commit is dropped (one Warning naming it) and the write goes on.
    [Fact]
    public async Task NextWrite_DeferredReportConflictsWithRequeue_ReportDroppedWritePushed()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (BusRepository repository, AcceptedJob job) = await ClaimedOnNodeAsync(ct);
        _nodeHooks.Override = spec => HookedProcessRunner.SubCommand(spec) == "push"
            ? new Zyggy.Core.Processes.ProcessResult(1, string.Empty, "! [rejected] main -> main (fetch first)", false, false, false, TimeSpan.Zero)
            : null;
        (await repository.ReportAsync(job, new ReportFields(ReportStatus.Done), Encoding.UTF8.GetBytes("ok\n"), TestKeys.Acme1, ct)).Should().BeOfType<Deferred>();
        _nodeHooks.Override = null;
        (await SweepAsync(ct)).Exit.Should().Be(0);
        Model context = await ContextAsync(ct);

        // Act
        WriteOutcome outcome = await repository.PublishContextAsync(context, ct);

        // Assert
        string state = await CheckoutStateAsync(_harness.Bus.CloneDir, ct);
        outcome.Should().BeOfType<Pushed>(state);
        (await RebaseInProgressAsync(_harness.Bus.CloneDir, ct)).Should().BeFalse(state);
        (await _harness.PorcelainAsync(_harness.Bus.CloneDir, ct)).Should().BeEmpty(state);
        (await _harness.Bus.RunGitAsync(_harness.Bus.CloneDir, ["rev-list", "--count", "origin/main..HEAD"], ct)).Trim().Should().Be("0", state);
        (await _harness.Bus.ExistsOnMainAsync(BusPaths.EnvelopeFile(BusPaths.Context(CliHarness.Acme, CliHarness.HomeLaptop), context.Header.Id), ct)).Should().BeTrue();
        (await ReportsOnMainAsync(ct)).Should().BeEmpty();
        (await _harness.Bus.ExistsOnMainAsync(BusPaths.EnvelopeFile(BusPaths.Jobs(CliHarness.Acme, CliHarness.HomeLaptop), job.File.Id), ct)).Should().BeTrue();
        _nodeLog.AtLevel(LogLevel.Warning).Should().ContainSingle(m => m.Message.Contains("acme report ", StringComparison.Ordinal));
    }

    private static string ClaimedPath(AcceptedJob job) => BusPaths.EnvelopeFile(BusPaths.Claimed(CliHarness.Acme, CliHarness.HomeLaptop), job.File.Id);

    private static Task<bool> RebaseInProgressAsync(string checkout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Directory.Exists(Path.Combine(checkout, ".git", "rebase-merge")) || Directory.Exists(Path.Combine(checkout, ".git", "rebase-apply")));
    }

    // A signed context of the home laptop for Central, created now.
    private async Task<Model> ContextAsync(CancellationToken ct)
    {
        byte[] bytes = await TestEnvelopes.SignedContextBytesAsync(CliHarness.HomeLaptop, CliHarness.Central, _harness.Store, TestKeys.Acme1, "a fact\n", TimeProvider.System.GetUtcNow(), ct);
        EnvelopeResult parsed = await new EnvelopeSigner(_harness.Store).ParseAndVerifyAsync(bytes, CliHarness.Acme, ct);
        parsed.IsAccepted.Should().BeTrue(parsed.Rejection?.ToString());
        return parsed.Envelope!;
    }

    // Central sweeps (pushes a re-queue of the node's claim) right before the node's first push.
    private Func<Zyggy.Core.Processes.ProcessSpec, Task> SweepBeforeFirstPush(CancellationToken ct)
    {
        bool swept = false;
        return async spec =>
        {
            if (!swept && HookedProcessRunner.SubCommand(spec) == "push")
            {
                swept = true;
                (await SweepAsync(ct)).Exit.Should().Be(0);
            }
        };
    }

    // A job central → home-laptop, pulled, verified and claimed (pushed) by the home laptop's repository.
    private async Task<(BusRepository Repository, AcceptedJob Job)> ClaimedOnNodeAsync(CancellationToken ct)
    {
        await _harness.Bus.SeedSignedJobAsync(CliHarness.Central, CliHarness.HomeLaptop, TestKeys.Acme1, _harness.Store, "probe\n", ct: ct);
        BusRepository repository = _node.GetRequiredService<BusRepository>();
        (await repository.PullAsync(ct)).Succeeded.Should().BeTrue();
        var job = (AcceptedJob)await repository.VerifyIntakeAsync((await repository.ListNewJobsAsync(ct)).Single(), ct);
        (await repository.ClaimAsync(job, ct)).Should().BeOfType<Pushed>();
        return (repository, job);
    }

    private async Task<string> CheckoutStateAsync(string checkout, CancellationToken ct) =>
        "status:\n" + await _harness.Bus.RunGitAsync(checkout, ["status"], ct)
        + "\nlog:\n" + await _harness.Bus.RunGitAsync(checkout, ["log", "--oneline", "-5", "--all"], ct);

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
