using System.Text;

using Microsoft.Extensions.Logging;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Tests.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Bus;

/// <summary>Spec 17 slice 1 (AC-1..AC-5a, AC-16, AC-17): what Central's sweep does with each stale claim, through the git and bus seams.</summary>
public sealed class SweepClaimsTests : IDisposable
{
    private static readonly string HomeJobs = BusPaths.Jobs(SweepClaimsHarness.Acme, SweepClaimsHarness.HomeLaptop);

    private static readonly string HomeRejected = BusPaths.Rejected(SweepClaimsHarness.Acme, SweepClaimsHarness.HomeLaptop);

    private static readonly string CentralReports = BusPaths.Reports(SweepClaimsHarness.Acme, SweepClaimsHarness.Central);

    private readonly SweepClaimsHarness _h = new();

    public void Dispose() => _h.Dispose();

    // AC-1: the limit is timeout_minutes + 15 min, Central's default timeout when the field is absent; only a claim strictly older is acted on.
    [Theory]
    [InlineData(2, 17 * 60, 0)]
    [InlineData(2, (17 * 60) + 1, 1)]
    [InlineData(null, 55 * 60, 0)]
    [InlineData(null, (55 * 60) + 1, 1)]
    public async Task RunAsync_ClaimAtOrPastItsLimit_ActsOnlyWhenStrictlyOlder(int? timeoutMinutes, int ageSeconds, int expected)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await _h.DropClaimAsync(SweepClaimsHarness.HomeLaptop, ct, timeoutMinutes: timeoutMinutes, claimedAgo: TimeSpan.FromSeconds(ageSeconds));
        BusSweep sweep = await _h.SweepAsync(ct, defaultTimeoutMinutes: 40);

        // Act
        SweepResult result = await sweep.RunAsync(ct);

        // Assert
        result.Requeued.Should().Be(expected);
        _h.Processes.CallsOf("commit").Should().HaveCount(expected);
    }

    // AC-2: re-queued with attempt + 1, signed with Central's key, every other field and the body unchanged, one requeue commit.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RunAsync_StaleVerifyingClaimBelowThree_RequeuedWithAttemptRaisedAndReSigned(int attempt)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (string path, Model job, _) = await _h.DropClaimAsync(SweepClaimsHarness.HomeLaptop, ct, attempt: attempt);
        string target = BusPaths.EnvelopeFile(HomeJobs, job.Header.Id);
        BusSweep sweep = await _h.SweepAsync(ct);

        // Act
        SweepResult result = await sweep.RunAsync(ct);

        // Assert
        result.Actions.Should().Equal(new SweepAction(SweepActionKind.Requeued, job.Header.Id, SweepClaimsHarness.HomeLaptop, attempt + 1, null));
        Model requeued = await _h.ReadVerifiedAsync(target, ct);
        requeued.Job!.Attempt.Should().Be(attempt + 1);
        requeued.KeyId.Should().Be(TestKeys.Acme2);
        SweepClaimsHarness.OtherFields(requeued).Should().Equal(SweepClaimsHarness.OtherFields(job));
        requeued.Body.ToArray().Should().Equal(job.Body.ToArray());
        _h.Processes.CallsOf("mv").Should().ContainSingle().Which.Arguments.Should().Equal("mv", "--", path, target);
        _h.Processes.CallsOf("commit").Should().ContainSingle().Which.StandardInput.Should().Be($"acme requeue {job.Header.Id} central→home-laptop");
    }

    // AC-3: the third (or a later) stale claim is removed and answered by Central's failed / attempts_exhausted report.
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public async Task RunAsync_StaleVerifyingClaimAtThreeOrMore_RemovedAndReportedExhausted(int attempt)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (string path, Model job, _) = await _h.DropClaimAsync(SweepClaimsHarness.HomeLaptop, ct, attempt: attempt);
        BusSweep sweep = await _h.SweepAsync(ct);

        // Act
        SweepResult result = await sweep.RunAsync(ct);

        // Assert
        result.Actions.Should().Equal(new SweepAction(SweepActionKind.Exhausted, job.Header.Id, SweepClaimsHarness.HomeLaptop, attempt, null));
        Model report = await _h.ReadVerifiedAsync(_h.Checkout.FilesUnder(CentralReports).Should().ContainSingle().Subject, ct);
        report.Report.Should().Be(new ReportFields(ReportStatus.Failed, "attempts_exhausted"));
        report.Header.From.Should().Be(SweepClaimsHarness.Central);
        report.Header.To.Should().Be(SweepClaimsHarness.WorkLaptop);
        report.Header.InReplyTo.Should().Be(job.Header.Id);
        report.KeyId.Should().Be(TestKeys.Acme2);
        report.BodyText.Should().Be($"machine: home-laptop\nattempts: {attempt}\nclaimed: 2026-11-14T11:00:00Z\n");
        _h.Processes.CallsOf("rm").Should().ContainSingle().Which.Arguments.Should().Equal("rm", "--", path);
        _h.Processes.CallsOf("commit").Should().ContainSingle().Which.StandardInput.Should().Be($"acme report {report.Header.Id} central→work-laptop");
        _h.Checkout.FilesUnder(HomeJobs).Should().BeEmpty();
    }

    // AC-4 (readable from): moved unchanged to jobs/rejected/, Central's rejected report carries the §4 reason, nothing is re-signed.
    [Fact]
    public async Task RunAsync_StaleClaimWithTamperedBody_MovedUnchangedAndReportedRejected()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (string path, Model job, byte[] bytes) = await _h.DropClaimAsync(SweepClaimsHarness.HomeLaptop, ct, mutate: b => Replace(b, "probe body", "tampered"));
        BusSweep sweep = await _h.SweepAsync(ct);

        // Act
        SweepResult result = await sweep.RunAsync(ct);

        // Assert
        result.Actions.Should().Equal(new SweepAction(SweepActionKind.Rejected, job.Header.Id, SweepClaimsHarness.HomeLaptop, 1, "invalid_signature"));
        _h.Processes.CallsOf("mv").Should().ContainSingle().Which.Arguments.Should().Equal("mv", "--", path, BusPaths.EnvelopeFile(HomeRejected, job.Header.Id));
        _h.Checkout.Read(path).Should().Equal(bytes);
        _h.Checkout.FilesUnder(HomeJobs).Should().BeEmpty();
        Model report = await _h.ReadVerifiedAsync(_h.Checkout.FilesUnder(CentralReports).Should().ContainSingle().Subject, ct);
        report.Report.Should().Be(new ReportFields(ReportStatus.Rejected, "invalid_signature"));
        report.Header.To.Should().Be(SweepClaimsHarness.WorkLaptop);
        report.Header.InReplyTo.Should().Be(job.Header.Id);
        _h.Processes.CallsOf("commit").Should().ContainSingle().Which.StandardInput.Should().Be($"acme reject {job.Header.Id} central→work-laptop");
    }

    // AC-4 (unreadable from): the move alone records the rejection; the limit of an unparsable file is Central's default.
    [Fact]
    public async Task RunAsync_StaleClaimThatIsNotAnEnvelope_MovedUnchangedWithoutReport()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        byte[] garbage = Encoding.UTF8.GetBytes("not an envelope\n");
        (string path, Model job, _) = await _h.DropClaimAsync(SweepClaimsHarness.HomeLaptop, ct, mutate: _ => garbage);
        BusSweep sweep = await _h.SweepAsync(ct);

        // Act
        SweepResult result = await sweep.RunAsync(ct);

        // Assert
        result.Actions.Should().Equal(new SweepAction(SweepActionKind.Rejected, job.Header.Id, SweepClaimsHarness.HomeLaptop, 1, "malformed"));
        _h.Processes.CallsOf("mv").Should().ContainSingle().Which.Arguments.Should().Equal("mv", "--", path, BusPaths.EnvelopeFile(HomeRejected, job.Header.Id));
        _h.Checkout.Read(path).Should().Equal(garbage);
        _h.Checkout.FilesUnder(CentralReports).Should().BeEmpty();
        _h.Processes.CallsOf("commit").Should().ContainSingle().Which.StandardInput.Should().Be($"acme reject {job.Header.Id} central→unknown");
    }

    // AC-4 ("verifies" includes to = the folder's machine): a validly signed job in another machine's claimed folder is rejected, not re-queued.
    [Fact]
    public async Task RunAsync_StaleClaimAddressedToAnotherMachine_RejectedAsWrongRecipient()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (_, Model job, _) = await _h.DropClaimAsync(SweepClaimsHarness.HomeLaptop, ct, to: SweepClaimsHarness.Central);
        BusSweep sweep = await _h.SweepAsync(ct);

        // Act
        SweepResult result = await sweep.RunAsync(ct);

        // Assert
        result.Actions.Should().Equal(new SweepAction(SweepActionKind.Rejected, job.Header.Id, SweepClaimsHarness.HomeLaptop, 1, "wrong_recipient"));
        _h.Checkout.FilesUnder(HomeJobs).Should().BeEmpty();
    }

    // AC-5 (claims half): without a usable signing key no claim is moved, removed or reported, and one Error says so.
    [Fact]
    public async Task RunAsync_CentralSigningKeyMissing_NoClaimTouchedOneError()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await _h.StoreAsync(ct, centralKey: false);
        await _h.DropClaimAsync(SweepClaimsHarness.HomeLaptop, ct);
        await _h.DropClaimAsync(SweepClaimsHarness.HomeLaptop, ct, attempt: 3);
        await _h.DropClaimAsync(SweepClaimsHarness.WorkLaptop, ct, mutate: _ => Encoding.UTF8.GetBytes("not an envelope\n"));
        BusSweep sweep = await _h.SweepAsync(ct);

        // Act
        SweepResult result = await sweep.RunAsync(ct);

        // Assert
        result.Actions.Should().BeEmpty();
        _h.Processes.Calls.Select(RecordingProcessRunner.SubCommand).Should().OnlyContain(c => c == "log");
        _h.Checkout.FilesUnder(CentralReports).Should().BeEmpty();
        _h.Log.AtLevel(LogLevel.Error).Should().ContainSingle().Which.Message.Should().StartWith("sweep: cannot sign: ");
    }

    // AC-5a: a claim whose claim time cannot be read is untouched and warned about once per process; the other claims are handled.
    [Fact]
    public async Task RunAsync_ClaimTimeUnreadableForOneClaim_LeftWithOneWarningAcrossPasses_OthersHandled()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (string unreadable, _, _) = await _h.DropClaimAsync(SweepClaimsHarness.HomeLaptop, ct);
        (string readable, _, _) = await _h.DropClaimAsync(SweepClaimsHarness.WorkLaptop, ct);
        _h.ClaimTimeUnreadable(unreadable);
        BusSweep sweep = await _h.SweepAsync(ct);
        await sweep.RunAsync(ct);

        // Act
        SweepResult second = await sweep.RunAsync(ct);

        // Assert
        second.Requeued.Should().Be(1);
        _h.Processes.CallsOf("mv").Select(c => c.Arguments[2]).Should().Equal(readable, readable);
        _h.Log.Messages.Where(m => m.Contains("unreadable", StringComparison.Ordinal)).Should().Equal($"sweep: claim time of {unreadable} unreadable; claim left");
    }

    // AC-16: a git step failing for one claim never throws; the pass returns a result, handles the rest and logs one Error per streak.
    [Fact]
    public async Task RunAsync_MoveFailsForOneClaim_ResultCarriesFailure_NextClaimHandled_OneErrorAcrossPasses()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (string failing, _, _) = await _h.DropClaimAsync(SweepClaimsHarness.HomeLaptop, ct);
        (_, Model handled, _) = await _h.DropClaimAsync(SweepClaimsHarness.WorkLaptop, ct);
        _h.FailMoveOf(failing);
        BusSweep sweep = await _h.SweepAsync(ct);
        await sweep.RunAsync(ct);

        // Act
        SweepResult second = await sweep.RunAsync(ct);

        // Assert
        second.Failure.Should().Be(new WriteFailed("mv", "fatal: the move was refused"));
        second.Actions.Should().Equal(new SweepAction(SweepActionKind.Requeued, handled.Header.Id, SweepClaimsHarness.WorkLaptop, 2, null));
        _h.Log.AtLevel(LogLevel.Error).Select(e => e.Message).Should().Equal("sweep: failed at mv: fatal: the move was refused");
    }

    // AC-16: an unexpected exception inside the pass never reaches the caller; it becomes the result's failure and one Error.
    [Fact]
    public async Task RunAsync_ProcessRunnerThrows_ReturnsFailureNamingTheExceptionType_OneError()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await _h.DropClaimAsync(SweepClaimsHarness.HomeLaptop, ct);
        BusSweep sweep = await _h.SweepAsync(ct);
        _h.Processes.Hook = _ => throw new InvalidOperationException("the process runner broke");

        // Act
        SweepResult result = await sweep.RunAsync(ct);

        // Assert
        result.Should().BeEquivalentTo(new SweepResult([], new WriteFailed("pass", "InvalidOperationException"), false));
        _h.Log.Entries.Select(e => (e.Level, e.Message)).Should().Equal((LogLevel.Error, "sweep: failed at pass: InvalidOperationException"));
    }

    // AC-17: each claim line of the Logging table is written once, at its level, with tenant, machine and phase sweep.
    [Fact]
    public async Task RunAsync_RequeueExhaustedAndRejected_OneLinePerEventWithScope()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (_, Model requeued, _) = await _h.DropClaimAsync(SweepClaimsHarness.Central, ct);
        (_, Model exhausted, _) = await _h.DropClaimAsync(SweepClaimsHarness.HomeLaptop, ct, attempt: 3);
        (_, Model rejected, _) = await _h.DropClaimAsync(SweepClaimsHarness.WorkLaptop, ct, mutate: b => Replace(b, "probe body", "tampered"));
        BusSweep sweep = await _h.SweepAsync(ct);

        // Act
        await sweep.RunAsync(ct);

        // Assert
        _h.Log.Entries.Select(e => (e.Level, e.Message)).Should().Equal(
            (LogLevel.Warning, $"job {requeued.Header.Id} on central stuck since 2026-11-14T11:00:00Z: re-queued, attempt 2"),
            (LogLevel.Error, $"job {exhausted.Header.Id} on home-laptop failed after 3 attempts"),
            (LogLevel.Warning, $"claimed job {rejected.Header.Id} on work-laptop does not verify (invalid_signature): rejected"));
        _h.Log.Entries.Should().OnlyContain(e => e.Scopes.OfType<Dictionary<string, object>>().Any(
            s => (string)s["tenant"] == "acme" && (string)s["machine"] == "central" && (string)s["phase"] == "sweep"));
    }

    // AC-17 / idempotence: a pass that finds nothing stale writes nothing and logs nothing.
    [Fact]
    public async Task RunAsync_NothingStale_NoWriteNoLogLine()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await _h.DropClaimAsync(SweepClaimsHarness.HomeLaptop, ct, claimedAgo: TimeSpan.FromMinutes(5));
        BusSweep sweep = await _h.SweepAsync(ct);

        // Act
        SweepResult result = await sweep.RunAsync(ct);

        // Assert
        result.Should().BeEquivalentTo(new SweepResult([], null, false));
        _h.Processes.Calls.Select(RecordingProcessRunner.SubCommand).Should().Equal("log");
        _h.Log.Entries.Should().BeEmpty();
    }

    private static byte[] Replace(byte[] bytes, string from, string to) =>
        Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace(from, to, StringComparison.Ordinal));
}
