using System.Text;

using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Processes;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Cli;

/// <summary>
/// Spec 17 slice 3 (AC-3, AC-4 wire, AC-10, AC-10a): <c>zyggy sweep</c> in process on Central's checkout of a bare repository ends a
/// stale claim at <c>attempt: 3</c> with Central's <c>attempts_exhausted</c> report, moves a stale claim that does not verify unchanged
/// to <c>jobs/rejected/</c> (with Central's <c>rejected</c> report when its sender can be read), leaves <c>zyggy verify</c> without new
/// drift, and exits 6 when a git step fails and 7 when the push stays refused.
/// </summary>
public sealed class SweepSettleTests : IAsyncLifetime
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

    // AC-3: the third stale claim ends in Central's failed / attempts_exhausted report; the claim is removed.
    [Fact]
    public async Task Sweep_StaleClaimAtAttemptThree_ExhaustedReportOnRemote()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model job = await SignedJobAsync(attempt: 3, ct);
        await SeedClaimAsync(job.Header.Id, EnvelopeWriter.Serialize(job), ct);

        // Act
        var (exit, console) = await SweepAsync(ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        console.Stdout.Should().Be($"exhausted {job.Header.Id.Value} home-laptop after 3 attempts\nsweep: 0 re-queued, 1 exhausted, 0 rejected, 0 archived\n");
        (await _harness.Bus.ExistsOnMainAsync(ClaimedPath(job.Header.Id), ct)).Should().BeFalse();
        Model report = await _harness.ParseOnMainAsync((await CentralReportsAsync(ct)).Should().ContainSingle().Subject, ct);
        report.Header.From.Should().Be(CliHarness.Central);
        report.Header.To.Should().Be(job.Header.From);
        report.Header.InReplyTo.Should().Be(job.Header.Id);
        report.Report!.Status.Should().Be(ReportStatus.Failed);
        report.Report.Reason.Should().Be("attempts_exhausted");
        (await _harness.Bus.LastCommitSubjectAsync(ct)).Trim().Should().Be($"acme report {report.Header.Id.Value} central→central");
    }

    // AC-4: a stale claim whose signature fails moves unchanged to jobs/rejected/ with Central's rejected report; it is not re-signed.
    [Fact]
    public async Task Sweep_TamperedStaleClaim_MovedUnchangedWithRejectedReport()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model job = await SignedJobAsync(attempt: 1, ct);
        await SeedClaimAsync(job.Header.Id, Tampered(job), ct);
        string claimedBlob = await BlobOnMainAsync(ClaimedPath(job.Header.Id), ct);

        // Act
        var (exit, console) = await SweepAsync(ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        console.Stdout.Should().Be($"rejected {job.Header.Id.Value} home-laptop invalid_signature\nsweep: 0 re-queued, 0 exhausted, 1 rejected, 0 archived\n");
        (await _harness.Bus.ExistsOnMainAsync(ClaimedPath(job.Header.Id), ct)).Should().BeFalse();
        (await BlobOnMainAsync(RejectedPath(job.Header.Id), ct)).Should().Be(claimedBlob, "the file moves unchanged and is never re-signed");
        Model report = await _harness.ParseOnMainAsync((await CentralReportsAsync(ct)).Should().ContainSingle().Subject, ct);
        report.Header.To.Should().Be(job.Header.From);
        report.Header.InReplyTo.Should().Be(job.Header.Id);
        report.Report!.Status.Should().Be(ReportStatus.Rejected);
        report.Report.Reason.Should().Be("invalid_signature");
        (await _harness.Bus.LastCommitSubjectAsync(ct)).Trim().Should().Be($"acme reject {job.Header.Id.Value} central→central");
    }

    // AC-4: without a readable from the stale claim still moves unchanged, and no report is written.
    [Fact]
    public async Task Sweep_StaleClaimWithoutFrom_MovedWithoutReport()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model job = await SignedJobAsync(attempt: 1, ct);
        await SeedClaimAsync(job.Header.Id, WithoutFrom(job), ct);
        string claimedBlob = await BlobOnMainAsync(ClaimedPath(job.Header.Id), ct);

        // Act
        var (exit, console) = await SweepAsync(ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        console.Stdout.Should().Be($"rejected {job.Header.Id.Value} home-laptop missing_field\nsweep: 0 re-queued, 0 exhausted, 1 rejected, 0 archived\n");
        (await BlobOnMainAsync(RejectedPath(job.Header.Id), ct)).Should().Be(claimedBlob);
        (await CentralReportsAsync(ct)).Should().BeEmpty();
        (await _harness.Bus.LastCommitSubjectAsync(ct)).Trim().Should().Be($"acme reject {job.Header.Id.Value} central→unknown");
    }

    // AC-10: after a re-queue, an exhausted report and a rejection, zyggy verify shows no new drift: the one FAIL is the
    // non-verifying file, at its rejected path, with the outcome it already had in jobs/claimed/. (Archive half: slice 6.)
    [Fact]
    public async Task Sweep_RequeueExhaustedRejected_VerifyReportsNoNewDrift()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model requeue = await SignedJobAsync(attempt: 1, ct);
        Model exhaust = await SignedJobAsync(attempt: 3, ct);
        Model reject = await SignedJobAsync(attempt: 1, ct);
        await SeedClaimAsync(requeue.Header.Id, EnvelopeWriter.Serialize(requeue), ct);
        await SeedClaimAsync(exhaust.Header.Id, EnvelopeWriter.Serialize(exhaust), ct);
        await SeedClaimAsync(reject.Header.Id, Tampered(reject), ct);
        var (_, before) = await _harness.RunAsync(["verify"], _harness.CentralNodeJson, ct: ct);
        var (sweepExit, sweep) = await SweepAsync(ct);
        sweepExit.Should().Be(0, sweep.Stderr);

        // Act
        var (_, after) = await _harness.RunAsync(["verify", "--verbose"], _harness.CentralNodeJson, ct: ct);

        // Assert
        string[] failedBefore = FailLines(before.Stdout);
        failedBefore.Should().Equal($"FAIL {ClaimedPath(reject.Header.Id)} invalid_signature (sig)");
        FailLines(after.Stdout).Should().Equal($"FAIL {RejectedPath(reject.Header.Id)} invalid_signature (sig)");
        after.Stdout.Should().NotContain(ClaimedPath(exhaust.Header.Id), "the exhausted claim is removed, not left beside its report");
        IReadOnlyList<string> reports = await CentralReportsAsync(ct);
        reports.Should().HaveCount(2, "the exhausted report and the rejection report");
        after.Stdout.Split('\n').Should().Contain(
            reports.Select(r => "OK " + r).Append("OK " + BusPaths.EnvelopeFile(BusPaths.Jobs(CliHarness.Acme, CliHarness.HomeLaptop), requeue.Header.Id)));
    }

    // AC-10a: a git step of the pass fails → exit 6; the claim is in place, the checkout clean, the remote without a new commit.
    // "mv" fails the move itself; "add" fails the step after it, so the moved claim and the written report must be put back.
    [Theory]
    [InlineData("mv")]
    [InlineData("add")]
    public async Task Sweep_GitStepFails_ExitSixClaimInPlace(string step)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model job = await SignedJobAsync(attempt: 1, ct);
        await SeedClaimAsync(job.Header.Id, Tampered(job), ct);
        int commits = await _harness.CommitCountOnMainAsync(ct);
        _harness.Hooks.Override = spec => HookedProcessRunner.SubCommand(spec) == step
            ? new ProcessResult(128, string.Empty, "fatal: refused by fixture", false, false, false, TimeSpan.Zero)
            : null;

        // Act
        var (exit, console) = await SweepAsync(ct);

        // Assert
        exit.Should().Be(6, console.Stderr);
        console.Stderr.Should().Contain("failed at " + step);
        File.Exists(BusPaths.Resolve(_harness.Bus.SenderDir, ClaimedPath(job.Header.Id))).Should().BeTrue();
        File.Exists(BusPaths.Resolve(_harness.Bus.SenderDir, RejectedPath(job.Header.Id))).Should().BeFalse();
        (await _harness.PorcelainAsync(_harness.Bus.SenderDir, ct)).Should().BeEmpty();
        (await _harness.CommitCountOnMainAsync(ct)).Should().Be(commits);
    }

    // AC-10a: the pass committed but every push is refused → exit 7; the commit is local, not on the remote.
    [Fact]
    public async Task Sweep_PushRefused_ExitSevenCommitLocal()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model job = await SignedJobAsync(attempt: 1, ct);
        await SeedClaimAsync(job.Header.Id, EnvelopeWriter.Serialize(job), ct);
        int commits = await _harness.CommitCountOnMainAsync(ct);
        _harness.Hooks.Override = spec => HookedProcessRunner.SubCommand(spec) == "push"
            ? new ProcessResult(1, string.Empty, "! [rejected] main -> main (fetch first)", false, false, false, TimeSpan.Zero)
            : null;

        // Act
        var (exit, console) = await SweepAsync(ct);

        // Assert
        exit.Should().Be(7, console.Stderr);
        console.Stdout.Should().StartWith($"requeued {job.Header.Id.Value} home-laptop attempt 2\n");
        (await _harness.Bus.RunGitAsync(_harness.Bus.SenderDir, ["log", "-1", "--format=%s"], ct)).Trim().Should().Be($"acme requeue {job.Header.Id.Value} central→home-laptop");
        (await _harness.CommitCountOnMainAsync(ct)).Should().Be(commits);
    }

    private static string ClaimedPath(EnvelopeId id) => BusPaths.EnvelopeFile(BusPaths.Claimed(CliHarness.Acme, CliHarness.HomeLaptop), id);

    private static string RejectedPath(EnvelopeId id) => BusPaths.EnvelopeFile(BusPaths.Rejected(CliHarness.Acme, CliHarness.HomeLaptop), id);

    private static string[] FailLines(string stdout) => [.. stdout.Split('\n').Where(l => l.StartsWith("FAIL ", StringComparison.Ordinal))];

    // The signed job with one body character changed after signing: parses, from readable, signature invalid.
    private static byte[] Tampered(Model job) =>
        Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(EnvelopeWriter.Serialize(job)).Replace("change nothing.", "change nothing!", StringComparison.Ordinal));

    // The signed job without its from line: does not parse (missing field), from unreadable.
    private static byte[] WithoutFrom(Model job) =>
        Encoding.UTF8.GetBytes(string.Join('\n', Encoding.UTF8.GetString(EnvelopeWriter.Serialize(job)).Split('\n').Where(l => !l.StartsWith("from: ", StringComparison.Ordinal))));

    // A job central → home-laptop at the given attempt, signed with the tenant key.
    private async Task<Model> SignedJobAsync(int attempt, CancellationToken ct)
    {
        Model job = await TestEnvelopes.SignedJobAsync(
            CliHarness.Central,
            CliHarness.HomeLaptop,
            _harness.Store,
            TestKeys.Acme1,
            "Read README.md and finish with a REPORT section; change nothing.\n",
            ct,
            fields: new JobFields(_project.Path, TimeoutMinutes: 2, AllowedTools: ["Read"]));
        if (attempt == 1)
        {
            return job;
        }

        EnvelopeResult signed = await new EnvelopeSigner(_harness.Store).SignAsync(job.WithAttempt(attempt), TestKeys.Acme1, ct);
        signed.IsAccepted.Should().BeTrue(signed.Rejection?.ToString());
        return signed.Envelope!;
    }

    // Puts bytes into home-laptop's jobs/claimed/, committed now from Central's checkout (the sender clone).
    private Task SeedClaimAsync(EnvelopeId id, byte[] bytes, CancellationToken ct) =>
        _harness.Bus.SeedRawFileAsync(ClaimedPath(id), bytes, "acme claim " + id.Value + " home-laptop", ct);

    private async Task<string> BlobOnMainAsync(string path, CancellationToken ct) =>
        (await _harness.Bus.RunGitAsync(_harness.Bus.BareDir, ["rev-parse", "main:" + path], ct)).Trim();

    private async Task<IReadOnlyList<string>> CentralReportsAsync(CancellationToken ct)
    {
        string output = await _harness.Bus.RunGitAsync(_harness.Bus.BareDir, ["ls-tree", "--name-only", "main", BusPaths.Reports(CliHarness.Acme, CliHarness.Central) + "/"], ct);
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
