using Zyggy.Core.Outlook;

using static Zyggy.Core.Tests.Outlook.OutlookGuardTestData;

namespace Zyggy.Core.Tests.Outlook;

// AC-10: the guard refuses a send with an attachment, a Bcc, an HTML body, a changed sender/account/reply-to, no recipient, too many
// recipients or a too-long body (quoted history included) — each with its reason, before any prompt, with one log row.
public sealed class OutlookGuardSendTests
{
    private static readonly string[] Colleague = ["colleague@example.test"];

    public static TheoryData<string, string> Rules() => new()
    {
        { "attachment", "attachments are not allowed" },
        { "bcc", "Bcc is not allowed" },
        { "html", "only plain-text bodies" },
        { "rich text", "only plain-text bodies" },
        { "sending account", "sender, account and reply-to cannot be changed" },
        { "sender", "sender, account and reply-to cannot be changed" },
        { "reply-to", "sender, account and reply-to cannot be changed" },
        { "no recipient", "no recipient" },
        { "too many recipients", "more than 10 recipients" },
        { "too long, quoted history included", "body over 4000 characters" },
        { "malformed address", "malformed address" },
    };

    private static OutlookSendRequest Breaking(string rule)
    {
        var reply = Reply();
        return rule switch
        {
            "attachment" => reply with { Attachments = ["invoice.pdf"] },
            "bcc" => reply with { Bcc = ["hidden@example.test"] },
            "html" => reply with { BodyFormat = OutlookBodyFormat.Html },
            "rich text" => reply with { BodyFormat = OutlookBodyFormat.RichText },
            "sending account" => reply with { SendingAccount = "other@example.test" },
            "sender" => reply with { Sender = "boss@example.test" },
            "reply-to" => reply with { ReplyTo = ["elsewhere@example.test"] },
            "no recipient" => reply with { To = [], Cc = [] },
            "too many recipients" => reply with { To = [.. Enumerable.Range(1, 6).Select(i => $"to{i}@example.test")], Cc = [.. Enumerable.Range(1, 5).Select(i => $"cc{i}@example.test")] },
            "too long, quoted history included" => reply with { Body = new string('w', 100) + "\n\n> " + new string('q', 3897) }, // 4001 characters, 3901 of them the quoted history
            _ => reply with { To = ["not an address"] },
        };
    }

    [Theory]
    [MemberData(nameof(Rules))]
    public void CheckSend_OutsidePolicy_RefusedWithItsReason(string rule, string reason)
    {
        // Arrange
        var guard = Guard(Mailbox(), new RecordingActionLog());

        // Act
        var decision = guard.CheckSend(Breaking(rule));

        // Assert
        decision.Reason.Should().Be(reason);
    }

    [Theory]
    [MemberData(nameof(Rules))]
    public void CheckSend_OutsidePolicy_WritesOneRefusedRow(string rule, string reason)
    {
        // Arrange
        var log = new RecordingActionLog();
        var guard = Guard(Mailbox(), log);

        // Act
        guard.CheckSend(Breaking(rule));

        // Assert
        log.Rows.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Action = OutlookAction.Send, Outcome = OutlookActionOutcome.Refused, Reason = reason, Time = Now });
    }

    [Theory]
    [MemberData(nameof(Rules))]
    public void CheckSend_OutsidePolicy_TouchesNothingInOutlook(string rule, string reason)
    {
        // Arrange
        _ = reason;
        var access = Mailbox();
        var guard = Guard(access, new RecordingActionLog());

        // Act
        guard.CheckSend(Breaking(rule));

        // Assert
        access.Calls.Should().BeEmpty();
    }

    [Fact]
    public void CheckSend_WithinPolicy_PassesToThePrompt()
    {
        // Arrange
        var guard = Guard(Mailbox(), new RecordingActionLog());

        // Act
        var decision = guard.CheckSend(Reply());

        // Assert
        decision.Passed.Should().BeTrue();
    }

    [Fact]
    public void CheckSend_WithinPolicy_WritesNoRowBeforeThePrompt()
    {
        // Arrange: the row after the prompt and the send is the action tool's (slice 7)
        var log = new RecordingActionLog();
        var guard = Guard(Mailbox(), log);

        // Act
        guard.CheckSend(Reply());

        // Assert
        log.Rows.Should().BeEmpty();
    }

    [Fact]
    public void CheckSend_BodyExactlyAtTheCapWithItsQuotedHistory_Passes()
    {
        // Arrange: 100 written + "\n\n> " (4) + 3895 quoted + "é" = 4000 characters
        var guard = Guard(Mailbox(), new RecordingActionLog());
        var request = Reply() with { Body = new string('w', 100) + "\n\n> " + new string('q', 3895) + "é" };

        // Act
        var decision = guard.CheckSend(request);

        // Assert
        decision.Passed.Should().BeTrue();
    }

    [Fact]
    public void CheckSend_TenRecipientsAcrossToAndCc_Passes()
    {
        // Arrange
        var guard = Guard(Mailbox(), new RecordingActionLog());
        var request = Reply() with { To = [.. Enumerable.Range(1, 5).Select(i => $"to{i}@example.test")], Cc = [.. Enumerable.Range(1, 5).Select(i => $"cc{i}@example.test")] };

        // Act
        var decision = guard.CheckSend(request);

        // Assert
        decision.Passed.Should().BeTrue();
    }

    [Fact]
    public void CheckSend_TightenedRecipientCap_RefusesAboveIt()
    {
        // Arrange
        var guard = Guard(Mailbox(), new RecordingActionLog(), settings: SendAndMove(maxRecipients: 1));
        var request = Reply() with { Cc = ["second@example.test"] };

        // Act
        var decision = guard.CheckSend(request);

        // Assert
        decision.Reason.Should().Be("more than 1 recipients");
    }

    [Fact]
    public void CheckSend_TightenedBodyCap_RefusesAboveIt()
    {
        // Arrange
        var guard = Guard(Mailbox(), new RecordingActionLog(), settings: SendAndMove(bodyMaxChars: 20));

        // Act
        var decision = guard.CheckSend(Reply());

        // Assert
        decision.Reason.Should().Be("body over 20 characters");
    }

    [Fact]
    public void CheckSend_RefusedRow_CarriesRecipientsSubjectAndReferenceNeverTheBody()
    {
        // Arrange
        var log = new RecordingActionLog();
        var guard = Guard(Mailbox(), log);
        var request = Reply() with { Body = "CANARY-BODY-58", Bcc = ["hidden@example.test"] };

        // Act
        guard.CheckSend(request);

        // Assert
        log.Rows.Single().Should().BeEquivalentTo(new
        {
            Recipients = Colleague,
            Subject = "RE: Planning",
            Reference = "m1",
            Destination = (string?)null,
        });
    }

    [Fact]
    public void CheckSend_AddressInTheRefusal_NeverEchoed()
    {
        // Arrange: a refusal never echoes a value of the request (spec 58 "Guard")
        var guard = Guard(Mailbox(), new RecordingActionLog());
        var request = Reply() with { To = ["CANARY-ADDRESS-58"] };

        // Act
        var decision = guard.CheckSend(request);

        // Assert
        decision.Reason.Should().NotContain("CANARY");
    }
}
