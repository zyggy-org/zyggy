using Zyggy.Core.Outlook;

using static Zyggy.Core.Tests.Outlook.OutlookGuardTestData;
using static Zyggy.Core.Tests.Outlook.OutlookTestData;

namespace Zyggy.Core.Tests.Outlook;

// Spec 58 action decision: no policy record → "policy not confirmed"; a read-only record → actions refused; outlook.enabled false →
// "outlook not enabled"; outlook.actions cannot exceed the policy scope — each before the prompt, with a log row, touching nothing.
public sealed class OutlookGuardPolicyTests
{
    private static readonly OutlookPolicyRecord ReadOnlyRecord = new(new DateOnly(2026, 11, 21), new HashSet<string>(["read"], StringComparer.Ordinal));

    public static TheoryData<string, string, string> Cases() => new()
    {
        { "no record", "send", "policy not confirmed" },
        { "no record", "move", "policy not confirmed" },
        { "read-only record", "send", "policy not confirmed" },
        { "read-only record", "move", "policy not confirmed" },
        { "disabled", "send", "outlook not enabled" },
        { "disabled", "move", "outlook not enabled" },
        { "actions empty", "send", "action not enabled" },
        { "actions empty", "move", "action not enabled" },
        { "only move enabled", "send", "action not enabled" },
        { "only send enabled", "move", "action not enabled" },
    };

    private static OutlookGuard Build(string setup, FakeOutlookAccess access, RecordingActionLog log) => setup switch
    {
        "no record" => Guard(access, log, policy: new OutlookPolicyDecision(null, OutlookPolicySource.None, Enabled: true)),
        "read-only record" => Guard(access, log, policy: new OutlookPolicyDecision(ReadOnlyRecord, OutlookPolicySource.WorkBoundaryFile, Enabled: true)),
        "disabled" => Guard(access, log, policy: new OutlookPolicyDecision(ReadSendMove, OutlookPolicySource.WorkBoundaryFile, Enabled: false)),
        "actions empty" => Guard(access, log, settings: OutlookActionSettings.ReadOnly),
        "only move enabled" => Guard(access, log, settings: SendAndMove() with { Actions = new HashSet<string>(["move"], StringComparer.Ordinal) }),
        _ => Guard(access, log, settings: SendAndMove() with { Actions = new HashSet<string>(["send"], StringComparer.Ordinal) }),
    };

    private static OutlookGuardDecision Check(OutlookGuard guard, string action) =>
        action == "send" ? guard.CheckSend(Reply()) : guard.CheckMove(new OutlookMoveRequest("m1", "Deleted Items"));

    [Theory]
    [MemberData(nameof(Cases))]
    public void Check_OutsideThePolicyRecordOrNodeJson_RefusedWithItsReason(string setup, string action, string reason)
    {
        // Arrange
        var guard = Build(setup, Mailbox(), new RecordingActionLog());

        // Act
        var decision = Check(guard, action);

        // Assert
        decision.Reason.Should().Be(reason);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Check_OutsideThePolicyRecordOrNodeJson_WritesOneRefusedRow(string setup, string action, string reason)
    {
        // Arrange
        var log = new RecordingActionLog();
        var guard = Build(setup, Mailbox(), log);

        // Act
        Check(guard, action);

        // Assert
        log.Rows.Should().ContainSingle().Which.Reason.Should().Be(reason);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Check_OutsideThePolicyRecordOrNodeJson_TouchesNothingInOutlook(string setup, string action, string reason)
    {
        // Arrange
        _ = reason;
        var access = Mailbox();
        var guard = Build(setup, access, new RecordingActionLog());

        // Act
        Check(guard, action);

        // Assert
        access.Calls.Should().BeEmpty();
    }

    [Fact]
    public void CheckSend_RecordScopesSendOnly_PassesSend()
    {
        // Arrange: the record's scope is per action
        var record = new OutlookPolicyRecord(new DateOnly(2026, 11, 21), new HashSet<string>(["read", "send"], StringComparer.Ordinal));
        var guard = Guard(Mailbox(), new RecordingActionLog(), policy: new OutlookPolicyDecision(record, OutlookPolicySource.NodeJsonMirror, Enabled: true));

        // Act
        var decision = guard.CheckSend(Reply());

        // Assert
        decision.Passed.Should().BeTrue();
    }
}
