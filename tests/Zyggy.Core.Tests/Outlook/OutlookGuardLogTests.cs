using System.Text.Json;

using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Outlook;

using static Zyggy.Core.Tests.Outlook.OutlookGuardTestData;

namespace Zyggy.Core.Tests.Outlook;

// Spec 58 "Log": one row per action call — time, action, outcome or refusal reason, item reference, and for a send the recipients
// and subject; never the body; the log stays on the laptop (a file in the outlook state directory).
public sealed class OutlookGuardLogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "zyggy-unit", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void ToJsonLine_RefusedSend_HasEveryFieldInvariant()
    {
        // Arrange
        var row = new OutlookActionLogRow(Now.ToOffset(TimeSpan.FromHours(1)), OutlookAction.Send, OutlookActionOutcome.Refused, "Bcc is not allowed", "m1", ["a@example.test", "b@example.test"], "RE: Planning", null);

        // Act
        var line = row.ToJsonLine();

        // Assert
        line.Should().Be("""{"ts":"2026-11-22T09:30:00Z","action":"send","outcome":"refused","reason":"Bcc is not allowed","reference":"m1","to":["a@example.test","b@example.test"],"subject":"RE: Planning"}""");
    }

    [Fact]
    public void ToJsonLine_DoneMove_NamesTheDestination()
    {
        // Arrange
        var row = new OutlookActionLogRow(Now, OutlookAction.Move, OutlookActionOutcome.Done, null, "m1", [], null, "Deleted Items");

        // Act
        var line = row.ToJsonLine();

        // Assert
        line.Should().Be("""{"ts":"2026-11-22T09:30:00Z","action":"move","outcome":"done","reference":"m1","destination":"Deleted Items"}""");
    }

    [Fact]
    public void Append_GuardRefusal_WritesOneLineWithoutTheBody()
    {
        // Arrange
        var log = new OutlookActionLog(_directory);
        var guard = new OutlookGuard(Mailbox(), OutlookTestData.Allowed, SendAndMove(), Attended, log, new FakeTimeProvider(Now));

        // Act
        guard.CheckSend(Reply() with { Body = "CANARY-BODY-58", Attachments = ["a.pdf"] });

        // Assert
        File.ReadAllText(log.FilePath).Should().Be("""{"ts":"2026-11-22T09:30:00Z","action":"send","outcome":"refused","reason":"attachments are not allowed","reference":"m1","to":["colleague@example.test"],"subject":"RE: Planning"}""" + "\n");
    }

    [Fact]
    public void Append_TwoRows_TwoValidJsonLines()
    {
        // Arrange
        var log = new OutlookActionLog(_directory);
        var row = new OutlookActionLogRow(Now, OutlookAction.Move, OutlookActionOutcome.Denied, null, "m1", [], null, "Archive");

        // Act
        log.Append(row);
        log.Append(row with { Outcome = OutlookActionOutcome.Failed, Reason = "failed: Outlook error" });

        // Assert
        File.ReadAllLines(log.FilePath).Select(l => JsonDocument.Parse(l).RootElement.GetProperty("outcome").GetString()).Should().Equal("denied", "failed");
    }
}
