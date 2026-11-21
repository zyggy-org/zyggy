using Zyggy.Core.Outlook;

using static Zyggy.Core.Tests.Outlook.OutlookGuardTestData;
using static Zyggy.Core.Tests.Outlook.OutlookTestData;

namespace Zyggy.Core.Tests.Outlook;

// AC-11: a send or move in a run started by the node, or in any unattended run, is refused "not attended" with a log row, whatever
// the job's allowed tools say. The job runner sets ZYGGY_HOOKS=off and ZYGGY_JOB last (JobRunnerInvocationTests); any other
// unattended run sets ZYGGY_HOOKS=off. The end-to-end job test through the action tool comes with slice 7's entry point.
public sealed class OutlookGuardAttendanceTests
{
    public static TheoryData<string, string?, string?> Environments() => new()
    {
        { "node job run", "off", "01M80669PBZSHK0SWDX8KCZQT3" },
        { "unattended run", "off", null },
        { "job id only", null, "01M80669PBZSHK0SWDX8KCZQT3" },
    };

    public static TheoryData<string> Actions() => ["send", "move"];

    private static OutlookRunContext Context(string? hooks, string? job)
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal) { ["PATH"] = "/usr/bin", ["ZYGGY_HOOKS"] = hooks, ["ZYGGY_JOB"] = job };
        return OutlookRunContext.FromEnvironment(environment);
    }

    private static OutlookGuardDecision Check(OutlookGuard guard, string action) =>
        action == "send" ? guard.CheckSend(Reply()) : guard.CheckMove(new OutlookMoveRequest("m1", "Deleted Items"));

    [Theory]
    [MemberData(nameof(Environments))]
    public void FromEnvironment_UnattendedRun_IsNotAttended(string run, string? hooks, string? job)
    {
        // Arrange
        _ = run;

        // Act
        var context = Context(hooks, job);

        // Assert
        context.Attended.Should().BeFalse();
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("on", null)]
    public void FromEnvironment_OwnerSession_IsAttended(string? hooks, string? job)
    {
        // Act
        var context = Context(hooks, job);

        // Assert
        context.Attended.Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public void Check_NotAttended_RefusedNotAttended(string action)
    {
        // Arrange: a policy, node.json and request that would all pass in an attended session
        var guard = Guard(Mailbox(), new RecordingActionLog(), run: Context("off", "01M80669PBZSHK0SWDX8KCZQT3"));

        // Act
        var decision = Check(guard, action);

        // Assert
        decision.Reason.Should().Be("not attended");
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public void Check_NotAttended_WritesOneRefusedRow(string action)
    {
        // Arrange
        var log = new RecordingActionLog();
        var guard = Guard(Mailbox(), log, run: Context("off", null));

        // Act
        Check(guard, action);

        // Assert
        log.Rows.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Outcome = OutlookActionOutcome.Refused, Reason = "not attended" });
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public void Check_NotAttended_TouchesNothingInOutlook(string action)
    {
        // Arrange
        var access = Mailbox();
        var guard = Guard(access, new RecordingActionLog(), run: Context("off", null));

        // Act
        Check(guard, action);

        // Assert
        access.Calls.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public void Check_NotAttendedAndNoPolicy_NotAttendedComesFirst(string action)
    {
        // Arrange: the action decision of spec 58 checks the run before the policy record
        var guard = Guard(Mailbox(), new RecordingActionLog(), policy: new OutlookPolicyDecision(null, OutlookPolicySource.None, Enabled: false), run: Context("off", null));

        // Act
        var decision = Check(guard, action);

        // Assert
        decision.Reason.Should().Be("not attended");
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public void Check_Attended_PassesToThePrompt(string action)
    {
        // Arrange
        var guard = Guard(Mailbox(), new RecordingActionLog(), run: Context(null, null));

        // Act
        var decision = Check(guard, action);

        // Assert
        decision.Passed.Should().BeTrue();
    }

    [Fact]
    public void Check_AllowedOnlyByPolicy_StillNeedsAttendance()
    {
        // Arrange: whatever the job's allowed tools say — nothing outside the environment can make a job run attended
        var guard = Guard(Mailbox(), new RecordingActionLog(), policy: Allowed, settings: SendAndMove(), run: Context("off", "01M80669PBZSHK0SWDX8KCZQT3"));

        // Act
        var decision = guard.CheckSend(Reply());

        // Assert
        decision.Passed.Should().BeFalse();
    }
}
