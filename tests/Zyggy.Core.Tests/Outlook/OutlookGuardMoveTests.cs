using Zyggy.Core.Outlook;

using static Zyggy.Core.Tests.Outlook.OutlookGuardTestData;

namespace Zyggy.Core.Tests.Outlook;

// AC-10: the guard refuses a move to another store, an excluded folder or a permanent delete — each with its reason, before any
// prompt, with one log row; a move it cannot check (unknown folder, item not in the default store, Outlook down) is refused too.
public sealed class OutlookGuardMoveTests
{
    private static readonly string[] Excluded = ["Private"];

    public static TheoryData<string, string> Rules() => new()
    {
        { "other store", "only the default store" },
        { "excluded folder", "folder excluded" },
        { "excluded folder, other case", "folder excluded" },
        { "subfolder of an excluded folder", "folder excluded" },
        { "permanent delete", "permanent delete not allowed" },
        { "recoverable items", "permanent delete not allowed" },
        { "unknown folder", "folder not found" },
        { "item of another store", "item not found" },
    };

    public static TheoryData<string> Allowed() => ["Deleted Items", "Archive", "Inbox", "Inbox/Projects"];

    private static OutlookMoveRequest Breaking(string rule) => rule switch
    {
        "other store" => new OutlookMoveRequest("m1", "Archive") { Store = FakeOutlookAccess.SharedStore },
        "excluded folder" => new OutlookMoveRequest("m1", "Private"),
        "excluded folder, other case" => new OutlookMoveRequest("m1", "PRIVATE"),
        "subfolder of an excluded folder" => new OutlookMoveRequest("m1", "Private/Health"),
        "permanent delete" => new OutlookMoveRequest("m1", "Deleted Items") { Permanent = true },
        "recoverable items" => new OutlookMoveRequest("m1", "Recoverable Items"),
        "unknown folder" => new OutlookMoveRequest("m1", "Nowhere"),
        _ => new OutlookMoveRequest("s1", "Archive"),
    };

    private static OutlookGuard MoveGuard(FakeOutlookAccess access, RecordingActionLog log) =>
        Guard(access, log, settings: SendAndMove(excluded: Excluded));

    [Theory]
    [MemberData(nameof(Rules))]
    public void CheckMove_OutsidePolicy_RefusedWithItsReason(string rule, string reason)
    {
        // Arrange
        var guard = MoveGuard(Mailbox(), new RecordingActionLog());

        // Act
        var decision = guard.CheckMove(Breaking(rule));

        // Assert
        decision.Reason.Should().Be(reason);
    }

    [Theory]
    [MemberData(nameof(Rules))]
    public void CheckMove_OutsidePolicy_WritesOneRefusedRow(string rule, string reason)
    {
        // Arrange
        var log = new RecordingActionLog();
        var guard = MoveGuard(Mailbox(), log);
        var request = Breaking(rule);

        // Act
        guard.CheckMove(request);

        // Assert
        log.Rows.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Action = OutlookAction.Move,
            Outcome = OutlookActionOutcome.Refused,
            Reason = reason,
            Reference = request.Reference,
            Destination = request.Destination,
            Time = Now,
        });
    }

    [Theory]
    [MemberData(nameof(Allowed))]
    public void CheckMove_AllowedDestination_PassesToThePrompt(string destination)
    {
        // Arrange
        var guard = MoveGuard(Mailbox(), new RecordingActionLog());

        // Act
        var decision = guard.CheckMove(new OutlookMoveRequest("m1", destination));

        // Assert
        decision.Passed.Should().BeTrue();
    }

    [Fact]
    public void CheckMove_WithinPolicy_WritesNoRowBeforeThePrompt()
    {
        // Arrange
        var log = new RecordingActionLog();
        var guard = MoveGuard(Mailbox(), log);

        // Act
        guard.CheckMove(new OutlookMoveRequest("m1", "Deleted Items"));

        // Assert
        log.Rows.Should().BeEmpty();
    }

    [Fact]
    public void CheckMove_WellKnownFolderListedAsExcluded_StillAllowed()
    {
        // Arrange: Deleted Items, Archive and Inbox are always allowed destinations (spec 58 "Move (file)"), as in the m365 guard
        var guard = Guard(Mailbox(), new RecordingActionLog(), settings: SendAndMove(excluded: ["Deleted Items"]));

        // Act
        var decision = guard.CheckMove(new OutlookMoveRequest("m1", "Deleted Items"));

        // Assert
        decision.Passed.Should().BeTrue();
    }

    [Fact]
    public void CheckMove_PermanentDelete_TouchesNothingInOutlook()
    {
        // Arrange
        var access = Mailbox();
        var guard = MoveGuard(access, new RecordingActionLog());

        // Act
        guard.CheckMove(new OutlookMoveRequest("m1", "Deleted Items") { Permanent = true });

        // Assert
        access.Calls.Should().BeEmpty();
    }

    [Fact]
    public void CheckMove_WithinPolicy_OnlyReadsTheDefaultStore()
    {
        // Arrange: the guard reads, it never moves (the move is slice 7's, after the prompt)
        var access = Mailbox();
        var guard = MoveGuard(access, new RecordingActionLog());

        // Act
        guard.CheckMove(new OutlookMoveRequest("m1", "Inbox/Projects"));

        // Assert
        access.Calls.Should().Equal(
            "DefaultStoreId",
            $"Count {FakeOutlookAccess.DefaultStore} Inbox/Projects",
            $"Find {FakeOutlookAccess.DefaultStore} m1");
    }

    [Fact]
    public void CheckMove_OutlookNotAvailable_RefusedFailClosed()
    {
        // Arrange
        var access = Mailbox();
        access.Unavailable = true;
        var guard = MoveGuard(access, new RecordingActionLog());

        // Act
        var decision = guard.CheckMove(new OutlookMoveRequest("m1", "Deleted Items"));

        // Assert
        decision.Reason.Should().Be("Outlook not available");
    }

    [Fact]
    public void CheckMove_OutlookNotAvailable_WritesOneRefusedRow()
    {
        // Arrange
        var access = Mailbox();
        access.Unavailable = true;
        var log = new RecordingActionLog();
        var guard = MoveGuard(access, log);

        // Act
        guard.CheckMove(new OutlookMoveRequest("m1", "Deleted Items"));

        // Assert
        log.Rows.Should().ContainSingle().Which.Reason.Should().Be("Outlook not available");
    }
}
