using System.Text;

using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Git;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Bus;

// AC-10 (every limit of the mandate refuses and is named), AC-11 (one counting rule, both ends, at each boundary) and AC-12
// (pause and resume), on files in a bus checkout. Now is 2026-11-15 12:00 UTC; the mandate was granted at 10:00.
public sealed class MandateCheckTests : IAsyncLifetime
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName Central = MachineName.Central;

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly MachineName WorkLaptop = MachineName.Parse("work-laptop");

    private static readonly DateTimeOffset Granted = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Now = Granted.AddHours(2);

    private static readonly EnvelopeId MandateId = EnvelopeId.New(Granted);

    private readonly TempCheckout _checkout = new(Acme, Central);

    private readonly FakeTimeProvider _clock = new(Now);

    private InMemorySecretStore _store = null!;

    private int _sequence;

    public async ValueTask InitializeAsync() => _store = await TestKeys.StoreWithBothAsync(CancellationToken.None);

    public ValueTask DisposeAsync()
    {
        _checkout.Dispose();
        return ValueTask.CompletedTask;
    }

    private static MandateFields Fields => new(
        "calizr",
        "Build the booking pool",
        ["Read", "Edit", "Bash(dotnet test *)"],
        3,
        Granted.AddDays(3),
        Branches: ["feature/12-booking-pool"],
        MaxJobsPerHour: 60,
        MaxJobsPerStep: 20);

    private static MandateSubmit Submit => new(HomeLaptop, "calizr", MandateJobKind.Build, "slice 2", ["Read", "Edit"], "feature/12-booking-pool");

    // AC-10
    [Fact]
    public async Task CheckSubmit_InsideEveryLimit_Allowed()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropMandateAsync(Fields, ct);

        // Act
        MandateVerdict verdict = MandateRules.CheckSubmit(await LedgerAsync(ct), Submit, Now);

        // Assert
        verdict.Should().Be(MandateVerdict.Ok);
    }

    [Fact]
    public async Task CheckSubmit_NoMandateFile_RefusedAbsent()
    {
        // Act
        var ct = TestContext.Current.CancellationToken;
        MandateVerdict verdict = MandateRules.CheckSubmit(await LedgerAsync(ct), Submit, Now);

        // Assert
        verdict.Limit.Should().Be(MandateLimit.Absent);
    }

    [Fact]
    public async Task CheckSubmit_MandateFileTamperedWith_RefusedUnverified()
    {
        // Arrange: the signed file with max_jobs raised by hand
        var ct = TestContext.Current.CancellationToken;
        string path = await DropMandateAsync(Fields, ct);
        _checkout.Drop(path, EnvelopeText.From(_checkout.Read(path)).WithValue("max_jobs", "200").ToBytes());

        // Act
        MandateVerdict verdict = MandateRules.CheckSubmit(await LedgerAsync(ct), Submit, Now);

        // Assert
        verdict.Limit.Should().Be(MandateLimit.Unverified);
    }

    [Fact]
    public async Task CheckSubmit_MandateInRevoked_RefusedRevoked()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropMandateAsync(Fields, ct, revoked: true);

        // Act
        MandateVerdict verdict = MandateRules.CheckSubmit(await LedgerAsync(ct), Submit, Now);

        // Assert
        verdict.Limit.Should().Be(MandateLimit.Revoked);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, MandateLimit.Expired)]
    public async Task CheckSubmit_AtOrPastExpires_RefusedFromTheInstantOfExpiry(int secondsPast, MandateLimit? expected)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropMandateAsync(Fields, ct);

        // Act
        MandateVerdict verdict = MandateRules.CheckSubmit(await LedgerAsync(ct), Submit, Fields.Expires.AddSeconds(secondsPast - 1));

        // Assert
        verdict.Limit.Should().Be(expected);
    }

    public static TheoryData<string, MandateLimit> OutsideOneLimit() => new()
    {
        { "machine", MandateLimit.Machine },
        { "project", MandateLimit.Project },
        { "tool", MandateLimit.Tool },
        { "no-tools", MandateLimit.Tool },
        { "base", MandateLimit.Base },
        { "kind-merge", MandateLimit.Grant },
        { "kind-release", MandateLimit.Grant },
        { "kind-deploy", MandateLimit.Grant },
        { "session", MandateLimit.Sessions },
        { "work-machine", MandateLimit.WorkMachine },
        { "capability", MandateLimit.Capability },
    };

    [Theory]
    [MemberData(nameof(OutsideOneLimit))]
    public async Task CheckSubmit_OutsideOneLimit_RefusedNamingIt(string what, MandateLimit expected)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropMandateAsync(Fields, ct);
        MandateSubmit submit = what switch
        {
            "machine" => Submit with { To = WorkLaptop },
            "project" => Submit with { Project = "other" },
            "tool" => Submit with { Tools = ["Read", "Bash(dotnet test tests)"] },
            "no-tools" => Submit with { Tools = [] },
            "base" => Submit with { Base = "main" },
            "kind-merge" => Submit with { Kind = MandateJobKind.Merge },
            "kind-release" => Submit with { Kind = MandateJobKind.Release },
            "kind-deploy" => Submit with { Kind = MandateJobKind.Deploy },
            "session" => Submit with { Session = true },
            "work-machine" => Submit with { TargetIsWorkMachine = true },
            _ => Submit with { TargetHasCapability = false },
        };

        // Act
        MandateVerdict verdict = MandateRules.CheckSubmit(await LedgerAsync(ct), submit, Now);

        // Assert
        verdict.Limit.Should().Be(expected);
        verdict.Detail.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData(MandateJobKind.Merge, MandateGrant.Merge)]
    [InlineData(MandateJobKind.Release, MandateGrant.Release)]
    [InlineData(MandateJobKind.Deploy, MandateGrant.Deploy)]
    public async Task CheckSubmit_KindWithItsGrant_Allowed(MandateJobKind kind, MandateGrant grant)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropMandateAsync(Fields with { Grants = [MandateGrant.Accept, grant] }, ct);

        // Act
        MandateVerdict verdict = MandateRules.CheckSubmit(await LedgerAsync(ct), Submit with { Kind = kind }, Now);

        // Assert
        verdict.Should().Be(MandateVerdict.Ok);
    }

    [Fact]
    public async Task CheckSubmit_SessionTaskUnderAMandateWithSessions_AllowedAlsoForAWorkMachine()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropMandateAsync(Fields with { Sessions = true }, ct);

        // Act
        MandateVerdict verdict = MandateRules.CheckSubmit(await LedgerAsync(ct), Submit with { Session = true, TargetIsWorkMachine = true }, Now);

        // Assert
        verdict.Should().Be(MandateVerdict.Ok);
    }

    [Fact]
    public async Task CheckSubmit_NoBase_AllowedWhateverTheBranches()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropMandateAsync(Fields, ct);

        // Act
        MandateVerdict verdict = MandateRules.CheckSubmit(await LedgerAsync(ct), Submit with { Base = null }, Now);

        // Assert
        verdict.Should().Be(MandateVerdict.Ok);
    }

    [Theory]
    [InlineData(59, MandateLimit.UsageLimit)]
    [InlineData(60, null)]
    public async Task CheckSubmit_NewestReportIsAUsageLimit_RefusedForSixtyMinutes(int minutesAgo, MandateLimit? expected)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropMandateAsync(Fields with { MaxJobs = 20 }, ct);
        await DropReportAsync(ReportStatus.Failed, Now.AddMinutes(-minutesAgo), ct, reason: "claude_error", body: "claude_error: rate_limit\nUsage limit reached.\n");

        // Act
        MandateVerdict verdict = MandateRules.CheckSubmit(await LedgerAsync(ct), Submit, Now);

        // Assert
        verdict.Limit.Should().Be(expected);
    }

    [Fact]
    public async Task CheckSubmit_UsageLimitFollowedByADoneReport_Allowed()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropMandateAsync(Fields with { MaxJobs = 20 }, ct);
        await DropReportAsync(ReportStatus.Failed, Now.AddMinutes(-30), ct, reason: "claude_error", body: "claude_error: rate_limit\n");
        await DropReportAsync(ReportStatus.Done, Now.AddMinutes(-10), ct);

        // Act
        MandateVerdict verdict = MandateRules.CheckSubmit(await LedgerAsync(ct), Submit, Now);

        // Assert
        verdict.Should().Be(MandateVerdict.Ok);
    }

    // AC-11
    [Theory]
    [InlineData(2, null)]
    [InlineData(3, MandateLimit.TotalCap)]
    public async Task CheckSubmit_MaxJobsThree_ThirdPassesFourthRefused(int alreadyCounted, MandateLimit? expected)
    {
        // Arrange: one queued, one claimed, then one reported done
        var ct = TestContext.Current.CancellationToken;
        await DropMandateAsync(Fields, ct);
        await DropJobAsync(BusFolder.Jobs, Now.AddMinutes(-90), ct);
        await DropJobAsync(BusFolder.Claimed, Now.AddMinutes(-80), ct);
        if (alreadyCounted == 3)
        {
            await DropReportAsync(ReportStatus.Done, Now.AddMinutes(-70), ct);
        }

        // Act
        MandateLedger ledger = await LedgerAsync(ct);
        MandateVerdict verdict = MandateRules.CheckSubmit(ledger, Submit, Now);

        // Assert
        ledger.Counted.Should().HaveCount(alreadyCounted);
        verdict.Limit.Should().Be(expected);
    }

    [Fact]
    public async Task Counted_RejectedJobDoesNotCount_ArchivedReportStillCounts_FailedReportCounts()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropMandateAsync(Fields, ct);
        await DropJobAsync(BusFolder.Rejected, Now.AddMinutes(-100), ct);
        await DropReportAsync(ReportStatus.Rejected, Now.AddMinutes(-99), ct, reason: "invalid_signature");
        await DropReportAsync(ReportStatus.Done, Granted.AddMinutes(5), ct, archived: true);
        await DropReportAsync(ReportStatus.Failed, Now.AddMinutes(-20), ct, reason: "timeout");

        // Act
        MandateLedger ledger = await LedgerAsync(ct);

        // Assert
        ledger.Counted.Should().HaveCount(2);
        ledger.Reports.Should().HaveCount(3);
        MandateRules.CheckSubmit(ledger, Submit, Now).Should().Be(MandateVerdict.Ok);
    }

    [Fact]
    public async Task Counted_JobsAndReportsOfAnotherMandateOrNone_Ignored()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropMandateAsync(Fields, ct);
        var other = EnvelopeId.New(Granted.AddMinutes(1));
        await DropJobAsync(BusFolder.Jobs, Now.AddMinutes(-9), ct, mandate: other);
        await DropJobAsync(BusFolder.Jobs, Now.AddMinutes(-8), ct, noMandate: true);
        await DropReportAsync(ReportStatus.Done, Now.AddMinutes(-7), ct, mandate: other);

        // Act
        MandateLedger ledger = await LedgerAsync(ct);

        // Assert
        ledger.Counted.Should().BeEmpty();
    }

    [Theory]
    [InlineData(59, MandateLimit.HourlyCap)]
    [InlineData(60, null)]
    [InlineData(61, null)]
    public async Task CheckSubmit_HourlyCapOfTwo_RefusedUntilTheHourMoved(int minutesSinceTheSecondJob, MandateLimit? expected)
    {
        // Arrange: two jobs created at the same instant, both already reported; a job created exactly 60 minutes ago is outside the hour
        var ct = TestContext.Current.CancellationToken;
        await DropMandateAsync(Fields with { MaxJobs = 20, MaxJobsPerHour = 2 }, ct);
        DateTimeOffset second = Now.AddMinutes(-minutesSinceTheSecondJob);
        await DropReportAsync(ReportStatus.Done, second.AddMinutes(2), ct, job: EnvelopeId.New(second));
        await DropReportAsync(ReportStatus.Done, second.AddMinutes(3), ct, job: EnvelopeId.New(second));

        // Act
        MandateVerdict verdict = MandateRules.CheckSubmit(await LedgerAsync(ct), Submit, Now);

        // Assert
        verdict.Limit.Should().Be(expected);
    }

    [Theory]
    [InlineData("slice 2", MandateLimit.StepCap)]
    [InlineData("slice 3", null)]
    public async Task CheckSubmit_StepCapOfTwo_RefusedForThatStepOnly(string step, MandateLimit? expected)
    {
        // Arrange: slice 2 has one job queued and one reported
        var ct = TestContext.Current.CancellationToken;
        await DropMandateAsync(Fields with { MaxJobs = 20, MaxJobsPerStep = 2 }, ct);
        await DropJobAsync(BusFolder.Jobs, Now.AddMinutes(-90), ct, step: "slice 2");
        await DropReportAsync(ReportStatus.Done, Now.AddMinutes(-80), ct, step: "slice 2");

        // Act
        MandateVerdict verdict = MandateRules.CheckSubmit(await LedgerAsync(ct), Submit with { Step = step }, Now);

        // Assert
        verdict.Limit.Should().Be(expected);
    }

    [Theory]
    [InlineData(2, null)]
    [InlineData(3, MandateLimit.TotalCap)]
    public async Task CheckIntake_MaxJobsThree_NodeAcceptsTheThirdAndRejectsAFourth(int otherJobs, MandateLimit? expected)
    {
        // Arrange: the job under check sits in jobs/ and is not counted against itself
        var ct = TestContext.Current.CancellationToken;
        await DropMandateAsync(Fields, ct);
        await DropReportAsync(ReportStatus.Done, Now.AddMinutes(-70), ct);
        await DropJobAsync(BusFolder.Claimed, Now.AddMinutes(-60), ct);
        if (otherJobs == 3)
        {
            await DropReportAsync(ReportStatus.Failed, Now.AddMinutes(-50), ct, reason: "timeout");
        }

        Model job = await DropJobAsync(BusFolder.Jobs, Now.AddMinutes(-1), ct);

        // Act
        MandateVerdict verdict = MandateRules.CheckIntake(await LedgerAsync(ct), job, HomeLaptop, Now);

        // Assert
        verdict.Limit.Should().Be(expected);
    }

    public static TheoryData<string, MandateLimit> IntakeOutsideOneLimit() => new()
    {
        { "absent", MandateLimit.Absent },
        { "revoked", MandateLimit.Revoked },
        { "tampered", MandateLimit.Unverified },
        { "expired", MandateLimit.Expired },
        { "machine", MandateLimit.Machine },
        { "project", MandateLimit.Project },
        { "tool", MandateLimit.Tool },
        { "no-tools", MandateLimit.Tool },
        { "base", MandateLimit.Base },
        { "no-worktree", MandateLimit.Worktree },
    };

    [Theory]
    [MemberData(nameof(IntakeOutsideOneLimit))]
    public async Task CheckIntake_OutsideOneLimit_RefusedNamingIt(string what, MandateLimit expected)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        if (what != "absent")
        {
            string path = await DropMandateAsync(what == "machine" ? Fields : Fields, ct, revoked: what == "revoked", to: what == "machine" ? WorkLaptop : HomeLaptop);
            if (what == "tampered")
            {
                _checkout.Drop(path, EnvelopeText.From(_checkout.Read(path)).WithValue("max_jobs", "200").ToBytes());
            }
        }

        JobFields fields = what switch
        {
            "project" => JobOf("slice 2") with { Project = "other" },
            "tool" => JobOf("slice 2") with { AllowedTools = ["Read", "Write"] },
            "no-tools" => JobOf("slice 2") with { AllowedTools = null },
            "base" => JobOf("slice 2") with { Base = "main" },
            "no-worktree" => JobOf("slice 2") with { Worktree = false },
            _ => JobOf("slice 2"),
        };
        Model job = await SignedJobAsync(fields, Now.AddMinutes(-1), ct);

        // Act
        MandateVerdict verdict = MandateRules.CheckIntake(await LedgerAsync(ct), job, HomeLaptop, what == "expired" ? Fields.Expires : Now);

        // Assert
        verdict.Limit.Should().Be(expected);
    }

    [Fact]
    public async Task CheckIntake_SessionTaskWithoutWorktree_Allowed_PauseAndHourlyCapNotTheNodesBusiness()
    {
        // Arrange: a paused mandate at its hourly cap; the node checks neither
        var ct = TestContext.Current.CancellationToken;
        await DropMandateAsync(Fields with { MaxJobs = 20, MaxJobsPerHour = 1, Sessions = true }, ct);
        for (int i = 3; i >= 1; i--)
        {
            await DropReportAsync(ReportStatus.Failed, Now.AddMinutes(-i), ct, reason: "timeout");
        }

        Model job = await SignedJobAsync(JobOf("slice 2") with { Worktree = false, Deliver = JobDelivery.Session }, Now, ct);

        // Act
        MandateVerdict verdict = MandateRules.CheckIntake(await LedgerAsync(ct), job, HomeLaptop, Now);

        // Assert
        verdict.Should().Be(MandateVerdict.Ok);
    }

    // AC-12
    [Theory]
    [InlineData("failed,timeout,rejected", MandateState.Paused)]
    [InlineData("done,failed,failed", MandateState.Active)]
    [InlineData("failed,done,failed,failed", MandateState.Active)]
    [InlineData("failed,failed", MandateState.Active)]
    [InlineData("done,failed,failed,failed", MandateState.Paused)]
    public async Task StateAt_ThreeNewestReportsNotDone_Paused(string statusesOldestFirst, MandateState expected)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropMandateAsync(Fields with { MaxJobs = 20 }, ct);
        string[] statuses = statusesOldestFirst.Split(',');
        for (int i = 0; i < statuses.Length; i++)
        {
            ReportStatus status = statuses[i] switch { "done" => ReportStatus.Done, "timeout" => ReportStatus.Timeout, "rejected" => ReportStatus.Rejected, _ => ReportStatus.Failed };
            await DropReportAsync(status, Now.AddMinutes(-60 + i), ct, reason: status == ReportStatus.Done ? null : "timeout");
        }

        // Act
        MandateLedger ledger = await LedgerAsync(ct);

        // Assert
        ledger.StateAt(Now).Should().Be(expected);
        MandateRules.CheckSubmit(ledger, Submit, Now).Limit.Should().Be(expected == MandateState.Paused ? MandateLimit.Paused : null);
    }

    // Spec 59 AC-7 (Q5): a report without model and num_turns means the model never started; it does not count toward the pause.
    // A trailing "?" marks such a report. The first row is 2026-11-21: two "git_error: base" failures and one real failure.
    [Theory]
    [InlineData("failed?,failed?,failed", MandateState.Active)]
    [InlineData("failed,failed?,failed,failed", MandateState.Paused)]
    [InlineData("done,failed?,failed?,failed?", MandateState.Active)]
    [InlineData("failed?,failed?,failed?,failed?", MandateState.Active)]
    [InlineData("failed,failed,done?,failed", MandateState.Paused)]
    public async Task StateAt_ReportsWithoutModelAndTurns_DoNotCountTowardThePause(string statusesOldestFirst, MandateState expected)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropMandateAsync(Fields with { MaxJobs = 20 }, ct);
        string[] statuses = statusesOldestFirst.Split(',');
        for (int i = 0; i < statuses.Length; i++)
        {
            bool modelStarted = !statuses[i].EndsWith('?');
            ReportStatus status = statuses[i].TrimEnd('?') == "done" ? ReportStatus.Done : ReportStatus.Failed;
            await DropReportAsync(status, Now.AddMinutes(-60 + i), ct, reason: status == ReportStatus.Done ? null : "git_error", modelStarted: modelStarted);
        }

        // Act
        MandateLedger ledger = await LedgerAsync(ct);

        // Assert
        ledger.StateAt(Now).Should().Be(expected);
        MandateRules.CheckSubmit(ledger, Submit, Now).Limit.Should().Be(expected == MandateState.Paused ? MandateLimit.Paused : null);
    }

    // Spec 59 AC-7: a report with only num_turns (a run cut before the model line) still counts as a model run.
    [Fact]
    public async Task StateAt_ThreeFailuresWithTurnsButNoModel_Paused()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropMandateAsync(Fields with { MaxJobs = 20 }, ct);
        for (int i = 0; i < 3; i++)
        {
            await DropReportAsync(ReportStatus.Timeout, Now.AddMinutes(-20 + i), ct, reason: "timeout", modelStarted: false, numTurns: 200);
        }

        // Act
        MandateState state = (await LedgerAsync(ct)).StateAt(Now);

        // Assert
        state.Should().Be(MandateState.Paused);
    }

    [Fact]
    public async Task StateAt_ResumeDecisionAfterThreeFailures_ActiveAgain_EarlierReportsNoLongerCountTowardsAPause()
    {
        // Arrange: three failures, the owner's resume, then two more failures
        var ct = TestContext.Current.CancellationToken;
        await DropMandateAsync(Fields with { MaxJobs = 20 }, ct);
        for (int i = 0; i < 3; i++)
        {
            await DropReportAsync(ReportStatus.Failed, Now.AddMinutes(-60 + i), ct, reason: "timeout");
        }

        await DropResumeAsync(Now.AddMinutes(-30), ct);
        await DropReportAsync(ReportStatus.Failed, Now.AddMinutes(-20), ct, reason: "timeout");
        await DropReportAsync(ReportStatus.Failed, Now.AddMinutes(-10), ct, reason: "timeout");

        // Act
        MandateLedger ledger = await LedgerAsync(ct);

        // Assert
        ledger.Resumed.Should().Be(Now.AddMinutes(-30));
        ledger.StateAt(Now).Should().Be(MandateState.Active);
        ledger.Counted.Should().HaveCount(5, "a resume lifts the pause; it does not give jobs back");
    }

    [Fact]
    public async Task StateAt_AThirdFailureAfterTheResume_PausedAgain()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropMandateAsync(Fields with { MaxJobs = 20 }, ct);
        await DropResumeAsync(Now.AddMinutes(-30), ct);
        for (int i = 0; i < 3; i++)
        {
            await DropReportAsync(ReportStatus.Failed, Now.AddMinutes(-20 + i), ct, reason: "timeout");
        }

        // Act
        MandateState state = (await LedgerAsync(ct)).StateAt(Now);

        // Assert
        state.Should().Be(MandateState.Paused);
    }

    [Fact]
    public async Task StateAt_Order_RevokedBeforeExpiredBeforePausedBeforeExhausted()
    {
        // Arrange: at its cap, with three failures
        var ct = TestContext.Current.CancellationToken;
        string path = await DropMandateAsync(Fields, ct);
        for (int i = 0; i < 3; i++)
        {
            await DropReportAsync(ReportStatus.Failed, Now.AddMinutes(-20 + i), ct, reason: "timeout");
        }

        // Act
        MandateLedger live = await LedgerAsync(ct);
        string revokedPath = BusPaths.EnvelopeFile(BusPaths.RevokedMandates(Acme), MandateId);
        _checkout.Drop(revokedPath, _checkout.Read(path));
        File.Delete(BusPaths.Resolve(_checkout.Root, path));
        MandateLedger revoked = await LedgerAsync(ct);

        // Assert
        live.StateAt(Now).Should().Be(MandateState.Paused);
        live.StateAt(Fields.Expires).Should().Be(MandateState.Expired);
        revoked.StateAt(Fields.Expires).Should().Be(MandateState.Revoked);
    }

    [Fact]
    public async Task StateAt_AtItsCapWithDoneReports_Exhausted()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropMandateAsync(Fields, ct);
        for (int i = 0; i < 3; i++)
        {
            await DropReportAsync(ReportStatus.Done, Now.AddMinutes(-20 + i), ct);
        }

        // Act
        MandateState state = (await LedgerAsync(ct)).StateAt(Now);

        // Assert
        state.Should().Be(MandateState.Exhausted);
    }

    [Fact]
    public void MandateLimit_EveryMember_HasItsOwnWireString()
    {
        // Act
        string[] wires = [.. Enum.GetValues<MandateLimit>().Select(MandateRules.ToWire)];

        // Assert
        wires.Should().OnlyHaveUniqueItems().And.OnlyContain(w => w.Length > 0 && w.All(c => char.IsAsciiLetterLower(c) || c == '_'));
        wires.Should().HaveCount(19);
    }

    // Spec 59 slice 6 tightening (Central, 2026-11-21): under a mandate a job continues only from a job that ran under the same mandate
    // and the same step. "same-step-report": the earlier job reported (its file is gone); "same-step-open": it is still in jobs/claimed/.
    [Theory]
    [InlineData("same-step-report", null)]
    [InlineData("same-step-open", null)]
    [InlineData("other-step", MandateLimit.Continue)]
    [InlineData("no-mandate", MandateLimit.Continue)]
    [InlineData("other-mandate", MandateLimit.Continue)]
    [InlineData("unknown", MandateLimit.Continue)]
    [InlineData("rejected", MandateLimit.Continue)]
    public async Task CheckSubmit_Continue_OnlyFromAJobOfTheSameMandateAndStep(string what, MandateLimit? expected)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropMandateAsync(Fields with { MaxJobs = 20 }, ct);
        EnvelopeId earlier = await DropEarlierAsync(what, ct);

        // Act
        MandateVerdict verdict = MandateRules.CheckSubmit(await LedgerAsync(ct), Submit with { Continue = earlier }, Now);

        // Assert
        verdict.Limit.Should().Be(expected, verdict.Detail);
    }

    [Theory]
    [InlineData("same-step-report", null)]
    [InlineData("same-step-open", null)]
    [InlineData("other-step", MandateLimit.Continue)]
    [InlineData("no-mandate", MandateLimit.Continue)]
    [InlineData("other-mandate", MandateLimit.Continue)]
    [InlineData("unknown", MandateLimit.Continue)]
    [InlineData("rejected", MandateLimit.Continue)]
    [InlineData("self", MandateLimit.Continue)]
    public async Task CheckIntake_Continue_OnlyFromAJobOfTheSameMandateAndStep(string what, MandateLimit? expected)
    {
        // Arrange: "self" — the job under intake names itself, and is already a counted job of the step (it sits in jobs/claimed/)
        var ct = TestContext.Current.CancellationToken;
        await DropMandateAsync(Fields with { MaxJobs = 20 }, ct);
        EnvelopeId self = NextId(Now.AddMinutes(-1));
        EnvelopeId earlier = await DropEarlierAsync(what, ct, self);
        Model job = await SignedJobAsync(JobOf("slice 2") with { Continue = earlier }, Now.AddMinutes(-1), ct, self);

        // Act
        MandateVerdict verdict = MandateRules.CheckIntake(await LedgerAsync(ct), job, HomeLaptop, Now);

        // Assert
        verdict.Limit.Should().Be(expected, verdict.Detail);
    }

    [Fact]
    public async Task CheckSubmit_ContinueFromAnotherStep_DetailNamesTheLimitInOneLine()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropMandateAsync(Fields with { MaxJobs = 20 }, ct);
        EnvelopeId earlier = await DropEarlierAsync("other-step", ct);

        // Act
        MandateVerdict verdict = MandateRules.CheckSubmit(await LedgerAsync(ct), Submit with { Continue = earlier }, Now);

        // Assert
        verdict.Detail.Should().Be("continue: " + earlier.Value + " is not a job of this mandate's step 'slice 2'").And.NotContain("\n");
    }

    /// <summary>
    /// The job a continue names, in the shape <paramref name="what"/> asks for; "unknown" drops nothing; "self" drops the job under
    /// check itself (id <paramref name="self"/>, continuing from its own id) in jobs/claimed/.
    /// </summary>
    private async Task<EnvelopeId> DropEarlierAsync(string what, CancellationToken ct, EnvelopeId? self = null)
    {
        if (what == "self")
        {
            return (await DropJobAsync(BusFolder.Claimed, Now.AddMinutes(-1), ct, id: self, continueFrom: self)).Header.Id;
        }

        EnvelopeId other = EnvelopeId.New(Granted.AddMinutes(1));
        EnvelopeId earlier = NextId(Now.AddMinutes(-40));
        switch (what)
        {
            case "same-step-report":
                await DropReportAsync(ReportStatus.Failed, Now.AddMinutes(-30), ct, reason: "timeout", job: earlier);
                break;
            case "same-step-open":
                return (await DropJobAsync(BusFolder.Claimed, Now.AddMinutes(-30), ct)).Header.Id;
            case "other-step":
                await DropReportAsync(ReportStatus.Failed, Now.AddMinutes(-30), ct, reason: "timeout", step: "slice 1", job: earlier);
                break;
            case "no-mandate":
                return (await DropJobAsync(BusFolder.Claimed, Now.AddMinutes(-30), ct, noMandate: true)).Header.Id;
            case "other-mandate":
                return (await DropJobAsync(BusFolder.Claimed, Now.AddMinutes(-30), ct, mandate: other)).Header.Id;
            case "rejected":
                await DropReportAsync(ReportStatus.Rejected, Now.AddMinutes(-30), ct, reason: "branch_refused", job: earlier, modelStarted: false);
                break;
            default:
                break;
        }

        return earlier;
    }

    private static JobFields JobOf(string step, EnvelopeId? mandate = null) =>
        new("calizr", Worktree: true, AllowedTools: ["Read", "Edit"], Mandate: mandate ?? MandateId, Step: step, Base: "feature/12-booking-pool");

    private async Task<MandateLedger> LedgerAsync(CancellationToken ct)
    {
        var signer = new EnvelopeSigner(_store);
        var git = new GitClient(new RecordingProcessRunner(), new GitClientOptions { Identity = new GitIdentity("zyggy (central)", "central@zyggy.org") }, _clock);
        var bus = new BusRepository(git, Substitute.For<IBusProvider>(), new BusIdentity(Acme, Central), _checkout.Root, signer, _clock, () => 0, new RecordingLogger<BusRepository>());
        return await new BusInventory(bus, _checkout.Root, signer, _clock, new RecordingLogger<BusInventory>()).MandateLedgerAsync(MandateId, ct);
    }

    private async Task<string> DropMandateAsync(MandateFields fields, CancellationToken ct, bool revoked = false, MachineName? to = null)
    {
        Model mandate = Model.CreateMandate(new EnvelopeHeader(Acme, MandateId, Central, to ?? HomeLaptop, Granted), fields);
        string path = BusPaths.EnvelopeFile(revoked ? BusPaths.RevokedMandates(Acme) : BusPaths.Mandates(Acme), MandateId);
        _checkout.Drop(path, await SignedAsync(mandate, ct));
        return path;
    }

    private async Task<Model> SignedJobAsync(JobFields fields, DateTimeOffset created, CancellationToken ct, EnvelopeId? id = null)
    {
        Model job = Model.CreateJob(new EnvelopeHeader(Acme, id ?? NextId(created), Central, HomeLaptop, created), fields, "Build the step.\n"u8.ToArray());
        return (await new EnvelopeSigner(_store).SignAsync(job, TestKeys.Acme1, ct)).Envelope!;
    }

    /// <summary>A signed job under the mandate dropped in home-laptop's jobs/, jobs/claimed/ or jobs/rejected/.</summary>
    private async Task<Model> DropJobAsync(
        BusFolder folder, DateTimeOffset created, CancellationToken ct, string step = "slice 2", EnvelopeId? mandate = null, bool noMandate = false, EnvelopeId? id = null, EnvelopeId? continueFrom = null)
    {
        JobFields fields = JobOf(step, mandate) with { Continue = continueFrom };
        Model job = await SignedJobAsync(noMandate ? fields with { Mandate = null } : fields, created, ct, id);
        string directory = folder switch
        {
            BusFolder.Claimed => BusPaths.Claimed(Acme, HomeLaptop),
            BusFolder.Rejected => BusPaths.Rejected(Acme, HomeLaptop),
            _ => BusPaths.Jobs(Acme, HomeLaptop),
        };
        _checkout.Drop(BusPaths.EnvelopeFile(directory, job.Header.Id), EnvelopeWriter.Serialize(job));
        return job;
    }

    /// <summary>A signed report of home-laptop on a job under the mandate whose file is gone, live or archived.</summary>
    private async Task DropReportAsync(
        ReportStatus status,
        DateTimeOffset created,
        CancellationToken ct,
        string? reason = null,
        string step = "slice 2",
        string body = "## REPORT\nsummary: ok\n",
        bool archived = false,
        EnvelopeId? mandate = null,
        EnvelopeId? job = null,
        bool modelStarted = true,
        int? numTurns = null)
    {
        // Spec 59 Q5: a report of a run where the model started carries model and num_turns; a pre-model failure carries neither.
        EnvelopeId id = NextId(created);
        Model report = Model.CreateReport(
            new EnvelopeHeader(Acme, id, HomeLaptop, Central, created, EnvelopePriority.Normal, job ?? NextId(created.AddMinutes(-2))),
            new ReportFields(status, reason, NumTurns: numTurns ?? (modelStarted ? 7 : null), Model: modelStarted ? "claude-test" : null, Mandate: mandate ?? MandateId, Step: step),
            Encoding.UTF8.GetBytes(body));
        string folder = archived ? BusPaths.Archive(Acme, created.Year, created.Month) : BusPaths.Reports(Acme, HomeLaptop);
        _checkout.Drop(BusPaths.EnvelopeFile(folder, id), await SignedAsync(report, ct));
    }

    private async Task DropResumeAsync(DateTimeOffset created, CancellationToken ct)
    {
        EnvelopeId id = NextId(created);
        Model decision = Model.CreateDecision(
            new EnvelopeHeader(Acme, id, Central, HomeLaptop, created, EnvelopePriority.Normal, MandateId),
            new DecisionFields(DecisionKind.Resume, DecisionBasis.Reported, "revoke the mandate", MandateId),
            "The owner resumed the mandate.\n"u8.ToArray());
        _checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Decisions(Acme, Central), id), await SignedAsync(decision, ct));
    }

    private async Task<byte[]> SignedAsync(Model envelope, CancellationToken ct)
    {
        EnvelopeResult signed = await new EnvelopeSigner(_store).SignAsync(envelope, TestKeys.Acme1, ct);
        return EnvelopeWriter.Serialize(signed.Envelope!);
    }

    /// <summary>A fresh id at <paramref name="at"/>; a millisecond is added per call so two files of one instant never collide.</summary>
    private EnvelopeId NextId(DateTimeOffset at) => EnvelopeId.New(at.AddMilliseconds(++_sequence));
}
