using System.Runtime.Versioning;
using System.Text.Json;

using Zyggy.Core.M365.Guard;

namespace Zyggy.Core.Tests.M365;

/// <summary>
/// The action log — <c>m365-log.sh</c> (spec 33 AC-22; bats "log (AC-36)"): one body-free row per action call, the summary and status
/// as its jq programs build them, appended under an exclusive lock.
/// </summary>
public sealed class ActionLogTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 10, 0, 0, TimeSpan.Zero);

    private readonly string _state = Path.Combine(Path.GetTempPath(), "zyggy-ut", Guid.NewGuid().ToString("N"), "m365");

    public static bool IsLinux => OperatingSystem.IsLinux();

    public void Dispose()
    {
        if (Directory.Exists(Path.GetDirectoryName(_state)))
        {
            Directory.Delete(Path.GetDirectoryName(_state)!, recursive: true);
        }
    }

    [Theory]
    [InlineData("post-send-ok", "send-shared-mailbox-mail", "ok")]
    [InlineData("post-upload-ok", "upload-file-content", "ok")]
    [InlineData("post-move-ok", "move-shared-mailbox-message", "ok")]
    [InlineData("post-send-error", "send-shared-mailbox-mail", "error: ErrorAccessDenied")]
    [InlineData("post-move-error-text", "move-shared-mailbox-message", "error: ErrorItemNotFound")]
    public void BuildRow_Fixture_ToolAndStatusAsShell(string fixture, string tool, string status)
    {
        // Act
        var row = Row(fixture);

        // Assert
        row.GetProperty("tool").GetString().Should().Be(tool);
        row.GetProperty("status").GetString().Should().Be(status);
        row.GetProperty("session_id").GetString().Should().Be("00000000-0000-0000-0000-000000000000");
        row.GetProperty("ts").GetString().Should().Be("2026-09-30T10:00:00Z");
        row.EnumerateObject().Select(p => p.Name).Should().Equal("ts", "session_id", "tool", "summary", "status");
    }

    [Fact]
    public void BuildRow_SendUploadMove_SummaryAsShell()
    {
        // Arrange
        var bodyLength = JsonDocument.Parse(File.ReadAllText(M365Run.Golden("fixtures", "hook-send-clean.json"))).RootElement
            .GetProperty("tool_input").GetProperty("body").GetProperty("Message").GetProperty("body").GetProperty("content").GetString()!.Length;

        // Assert
        Summary("post-send-ok").Should().Be($"to carol@example.org,dave@example.org subject \"Zyggy D7 test\" body {bodyLength} chars");
        Summary("post-upload-ok").Should().Be("drive b!onedrive0001 parent 01PARENT0001 name notes.md size 1024");
        Summary("post-move-ok").Should().Be("message m1 -> archive");
    }

    [Fact]
    public void BuildRow_OtherTool_Null()
    {
        // Assert
        ActionLog.BuildRow(Hook("other-tool"), Now).Should().BeNull();
    }

    [Theory]
    [InlineData("post-send-ok")]
    [InlineData("post-upload-ok")]
    [InlineData("post-send-error")]
    public void BuildRow_NeverContainsBodyOrContent(string fixture)
    {
        // Act
        var row = ActionLog.BuildRow(Hook(fixture), Now)!;

        // Assert
        row.Should().NotContainAny("BODYTEXT-NEVER-STORED", "Hello Carol", "UPLOADTEXT-NEVER-LOGGED", "VVBMT0FEVEVYVC1ORVZFUi1MT0dHRUQ");
    }

    [Fact]
    public void BuildRow_SubjectCut120AndControlCharactersFlattened()
    {
        // Arrange
        var hook = File.ReadAllText(M365Run.Golden("fixtures", "hook-post-send-ok.json"))
            .Replace("\"subject\":\"Zyggy D7 test\"", "\"subject\":\"a\\tb\\n" + new string('s', 200) + "\"", StringComparison.Ordinal);

        // Act
        var summary = JsonDocument.Parse(ActionLog.BuildRow(JsonDocument.Parse(hook).RootElement, Now)!).RootElement.GetProperty("summary").GetString()!;

        // Assert
        summary.Should().Contain("subject \"a b " + new string('s', 116) + "\" body");
    }

    [Theory]
    [InlineData("""{"isError":false,"is_error":true}""", "error: unknown")]
    [InlineData("""{"content":[{"type":"text","text":"{\"error\":{\"status\":503}}"}]}""", "error: 503")]
    [InlineData("""{"content":[{"type":"text","text":"{\"error\":\"bad thing!\"}"}]}""", "error: badthing")]
    [InlineData("""{"content":[{"type":"text","text":"not json"}]}""", "ok")]
    [InlineData("""{"isError":true}""", "error: unknown")]
    public void BuildRow_Status_JqRules(string response, string status)
    {
        // Arrange
        var hook = $$$"""{"session_id":"s","tool_name":"mcp__m365__move-shared-mailbox-message","tool_input":{"messageId":"m1","body":{"DestinationId":"archive"}},"tool_response":{{{response}}}}""";

        // Act
        var row = JsonDocument.Parse(ActionLog.BuildRow(JsonDocument.Parse(hook).RootElement, Now)!).RootElement;

        // Assert
        row.GetProperty("status").GetString().Should().Be(status);
    }

    [Fact]
    public void Append_CreatesStateDirectoryAndOneLinePerRow()
    {
        // Act
        new ActionLog(_state).Append("""{"a":1}""");
        new ActionLog(_state).Append("""{"a":2}""");

        // Assert
        File.ReadAllText(Path.Combine(_state, "actions.jsonl")).Should().Be("{\"a\":1}\n{\"a\":2}\n");
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "Unix file modes: Linux only")]
    [SupportedOSPlatform("linux")]
    public void Append_OnLinux_File0600Dir0700()
    {
        // Act
        new ActionLog(_state).Append("""{"a":1}""");

        // Assert
        File.GetUnixFileMode(Path.Combine(_state, "actions.jsonl")).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.GetUnixFileMode(_state).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Fact]
    public async Task Append_TwoWriters_RowsNeverInterleave()
    {
        // Act
        var log = new ActionLog(_state);
        var token = TestContext.Current.CancellationToken;
        await Task.WhenAll(
            Task.Run(() => Enumerable.Range(0, 200).ToList().ForEach(i => log.Append($$"""{"writer":"a","i":{{i}},"pad":"{{new string('a', 300)}}"}""")), token),
            Task.Run(() => Enumerable.Range(0, 200).ToList().ForEach(i => new ActionLog(_state).Append($$"""{"writer":"b","i":{{i}},"pad":"{{new string('b', 300)}}"}""")), token));

        // Assert
        var lines = File.ReadAllLines(Path.Combine(_state, "actions.jsonl"));
        lines.Should().HaveCount(400);
        lines.Should().OnlyContain(l => JsonDocument.Parse(l, default).RootElement.GetProperty("i").ValueKind == JsonValueKind.Number);
    }

    private static JsonElement Hook(string name) => JsonDocument.Parse(File.ReadAllText(M365Run.Golden("fixtures", $"hook-{name}.json"))).RootElement;

    private static JsonElement Row(string fixture) => JsonDocument.Parse(ActionLog.BuildRow(Hook(fixture), Now)!).RootElement;

    private static string Summary(string fixture) => Row(fixture).GetProperty("summary").GetString()!;
}
