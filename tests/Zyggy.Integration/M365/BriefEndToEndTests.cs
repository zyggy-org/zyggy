using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.M365;

/// <summary>
/// Spec 35 (I, in process): <c>zyggy m365 brief</c> through the real process runner and <c>tools/fake-claude</c> — the captured call
/// carries the schema, the settings without auto memory and the deny list; <c>mail.json</c> is in the run directory while the model runs;
/// the brief file and its item list land on disk with their modes; a malformed answer leaves nothing behind; a stop leaves no model
/// process and no run directory. Tuesday 2026-10-06, 08:30Z; the stub's Inbox holds m10..m13.
/// </summary>
public sealed partial class BriefEndToEndTests : IDisposable
{
    private const string DraftsRoute = "mailFolders/drafts/messages\\?\\$filter";
    private const string ReplyDraft = """{"id":"d1","subject":"RE: Invoice 2026-41","toRecipients":[{"emailAddress":{"name":"Carol","address":"carol@example.org"}}],"ccRecipients":[],"bccRecipients":[],"conversationId":"c1","createdDateTime":"2026-10-06T08:31:00Z","changeKey":"CK1","isDraft":true,"body":{"contentType":"text","content":"Dear Carol, I will call tomorrow.\n"}}""";

    private readonly M365RunHarness _run = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 6, 8, 30, 0, TimeSpan.Zero));

    public BriefEndToEndTests()
    {
        // The watermark of the night, m1 recorded as replied (the stub's d1 reply Draft answers it), no brief Draft
        File.WriteAllText(Path.Combine(_run.State, "mail-watermark"), "2026-10-06T04:30:00Z\n");
        File.WriteAllText(Path.Combine(_run.State, "replied-2026-10-06.ids"), "m1\n");
        // yesterday's receipt names the reply Draft d5: the pre-pass's discard candidate (Z3)
        File.WriteAllText(Path.Combine(_run.State, "brief-2026-10-05.json"), """{"date":"2026-10-05","window_start":"2026-10-05T04:30:00Z","drafts":[{"id":"d5","kind":"reply","subject":"RE: Old thread","recipients":[]}],"replied_ids":[],"audit":"ok","reasons":[]}""");
        _run.Graph.Once("GET", DraftsRoute, 200, "{\"value\":[" + ReplyDraft + "]}");
    }

    private string BriefDirectory => Path.Combine(_run.Fixture.StateDirectory, "brief");

    private string Markdown => Path.Combine(BriefDirectory, "brief-2026-10-06.md");

    private string Sidecar => Path.Combine(BriefDirectory, "brief-2026-10-06.json");

    public void Dispose() => _run.Dispose();

    private static string[] Lines(string file) => File.ReadAllLines(M365InstanceFixture.Golden("m365", "run-lists", file));

    [GeneratedRegex(@"^- (! )?[0-9]{2}:[0-9]{2} Sender ", RegexOptions.Multiline)]
    private static partial Regex MailLine();

    private Task<(int Exit, VerbConsole Console)> BriefAsync(ActingModelRunner model, CancellationToken cancellationToken) =>
        _run.RunAsync(model, ["brief"], cancellationToken, _clock);

    [Fact]
    public async Task Brief_Weekday_FilesOnDiskJournalReceiptNoDraftCall()
    {
        // Arrange
        var model = _run.Model(_ => "m365-brief-mail-ok");

        // Act
        var (exit, console) = await BriefAsync(model, TestContext.Current.CancellationToken);

        // Assert
        exit.Should().Be(0, console.Stderr + console.Stdout);
        console.Stdout.Should().EndWith(File.ReadAllText(M365InstanceFixture.Golden("m365", "expected", "journal-weekday.txt")));
        console.Stderr.Should().Be("key: file\n");
        File.ReadAllText(Markdown).Split('\n')[0].Should().StartWith("Zyggy — morning brief 2026-10-06 (4 new mails since 2026-10-06T04:30:00Z: 1 urgent, 3 important, 0 other; 1 changed file)");
        var sidecar = JsonDocument.Parse(File.ReadAllText(Sidecar)).RootElement;
        sidecar.GetProperty("items").GetArrayLength().Should().Be(3);
        sidecar.GetProperty("audit").GetString().Should().Be("ok");
        JsonDocument.Parse(File.ReadAllText(Path.Combine(_run.State, "brief-2026-10-06.json"))).RootElement.GetProperty("drafts").GetArrayLength().Should().Be(1, "the reply Draft; no brief Draft");
        File.ReadAllText(Path.Combine(_run.State, "mail-watermark")).TrimEnd('\n').Should().Be("2026-10-06T08:15:00Z");
        File.ReadAllLines(Path.Combine(_run.Fixture.MemoryRoot, "acme", "alice", "inbox", "remember-2026-10-06.md")).Should().Contain(
            "- [observed] 2026-10-06 [m365-brief 2026-10-06]: Morning brief 2026-10-06 written: mail 4 (1 urgent, 3 important, 0 other), files 1, replies 1, z 3, you 1, ideas 0, facts 2, audit ok");
        _run.Graph.Requests.Should().NotContain(r => r.Method == HttpMethod.Post && r.Uri.ToString().Contains("/messages", StringComparison.Ordinal), "no Draft is created by the binary");
        _run.RunDirectories.Should().BeEmpty();
        if (OperatingSystem.IsLinux())
        {
            File.GetUnixFileMode(Markdown).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.GetUnixFileMode(Sidecar).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.GetUnixFileMode(BriefDirectory).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task Brief_WithRequestFilePresent_FileDeletedBeforeTheRun()
    {
        // Arrange: the session asked for a brief (zyggy brief request); the path unit started this run
        var request = Path.Combine(_run.Fixture.StateDirectory, "brief.request");
        File.WriteAllText(request, string.Empty);
        var model = _run.Model(_ => "m365-brief-mail-ok");

        // Act
        var (exit, console) = await BriefAsync(model, TestContext.Current.CancellationToken);

        // Assert
        exit.Should().Be(0, console.Stderr + console.Stdout);
        File.Exists(request).Should().BeFalse("a run that leaves the file would be restarted by the path unit");
        File.Exists(Markdown).Should().BeTrue();
    }

    [Fact]
    public async Task Brief_80Mails_PageViewWithinCaps_FullViewComplete_SidecarCountsPageExceededFalse()
    {
        // Arrange: the Inbox holds m20..m99 (5 urgent, 60 important, 15 other), the model classes them all
        _run.Graph.Once("GET", "mailFolders/AQMkInbox0001/messages\\?\\$filter=receivedDateTime", 200, File.ReadAllText(M365InstanceFixture.Golden("m365", "graph", "brief", "inbox-since-80.json")));
        var model = _run.Model(_ => "m365-brief-mail-80");

        // Act
        var (exit, console) = await BriefAsync(model, TestContext.Current.CancellationToken);
        var page = await ShowAsync();
        var full = await ShowAsync("--full");

        // Assert
        exit.Should().Be(0, console.Stderr + console.Stdout);
        console.Stdout.Should().Contain("mail 80 (5 urgent, 60 important, 15 other)").And.NotContain("page exceeded");
        var pageBody = Body(page.Stdout);
        pageBody.Split('\n').Length.Should().BeLessThanOrEqualTo(40);
        pageBody.Length.Should().BeLessThanOrEqualTo(3500);
        pageBody.Should().MatchRegex("… and [0-9]+ more important mails — say \"brief full\"");
        MailLine().Matches(pageBody).Count(m => m.Value.StartsWith("- ! ", StringComparison.Ordinal)).Should().Be(5, "urgent lines are never dropped");
        pageBody.Should().Contain("- 15 other mails, none needing you → Z2", "Z1 is the discard item of the old reply draft");
        var fullBody = Body(full.Stdout);
        MailLine().Count(fullBody).Should().Be(65, "the complete form keeps every urgent and important line");
        fullBody.Should().NotContain("brief full");
        var sidecar = JsonDocument.Parse(File.ReadAllText(Sidecar)).RootElement;
        sidecar.GetProperty("counts").GetProperty("important").GetInt32().Should().Be(60);
        sidecar.GetProperty("page_exceeded").GetBoolean().Should().BeFalse();
        sidecar.GetProperty("items")[1].GetProperty("kind").GetString().Should().Be("file-other");
        sidecar.GetProperty("items")[1].GetProperty("messageIds").GetArrayLength().Should().Be(15);
        File.ReadAllText(Path.Combine(_run.State, "mail-watermark")).TrimEnd('\n').Should().Be("2026-10-06T06:19:00Z");
    }

    [Fact]
    public async Task Brief_CapturedArgs_JsonSchemaSettingsAutoMemoryOffDenyListPromptOnStdin()
    {
        // Arrange
        var model = _run.Model(_ => "m365-brief-mail-ok");

        // Act
        var (exit, console) = await BriefAsync(model, TestContext.Current.CancellationToken);

        // Assert
        exit.Should().Be(0, console.Stderr + console.Stdout);
        var runDirectory = model.Requests[0].Environment["ZYGGY_M365_RUN_DIR"];
        string[] expected =
        [
            "-p", "--output-format", "stream-json", "--verbose", "--permission-mode", "auto", "--permission-prompts", "none",
            "--no-session-persistence", "--max-turns", "40", "--max-budget-usd", "3.0",
            "--allowedTools", string.Join(',', [.. Lines("brief-allow.txt"), $"Read({runDirectory}/**)"]),
            "--disallowedTools", string.Join(',', [.. Lines("brief-deny.txt"), $"Read(//{Path.Join(_run.Fixture.Checkout, "memory").Replace('\\', '/').TrimStart('/')}/**)"]),
            "--json-schema", new Core.Brief.BriefPrompts().MailSchema,
            "--settings", """{"autoMemoryEnabled":false}""",
            "--strict-mcp-config", "--mcp-config", Path.Join(_run.Fixture.StateDirectory, "m365", "run-mcp.json"),
        ];
        var capture = FakeClaude.ReadCapture(model.ArgumentsCapture(0));
        capture.Arguments.Should().Equal(expected);
        capture.Arguments.Should().NotContain(a => a.Contains("bypass", StringComparison.OrdinalIgnoreCase));
        Path.GetFullPath(capture.WorkingDirectory).Should().Be(Path.GetFullPath(_run.Fixture.Checkout));
        var stdin = Encoding.UTF8.GetString(File.ReadAllBytes(model.StdinCapture(0)));
        stdin.Should().Be($"/morning-brief alice@acme.example AQMkInbox0001 attachments=on b!onedrive0001 b!ops0001 b!opsarchive0001 {runDirectory}");
    }

    [Fact]
    public async Task Brief_CapturedArgs_RunMcpConfigOnlyM365_DenyIncludesLinkedIn()
    {
        // Arrange: spec 36 AC-8 — the fixture's .mcp.json names m365 and linkedin; the run's copy is read while the model runs
        string? loaded = null;
        var model = _run.Model(_ => "m365-brief-mail-ok", act: (request, _, _) => loaded = File.ReadAllText(request.McpConfig!));

        // Act
        var (exit, console) = await BriefAsync(model, TestContext.Current.CancellationToken);

        // Assert
        exit.Should().Be(0, console.Stderr + console.Stdout);
        var arguments = FakeClaude.ReadCapture(model.ArgumentsCapture(0)).Arguments;
        arguments.Should().ContainInConsecutiveOrder("--strict-mcp-config", "--mcp-config", Path.Join(_run.Fixture.StateDirectory, "m365", "run-mcp.json"));
        arguments[arguments.ToList().IndexOf("--disallowedTools") + 1].Split(',').Should().Contain(["mcp__linkedin__*", "Bash(zyggy linkedin *)"]);
        loaded.Should().Be(File.ReadAllText(M365InstanceFixture.Golden("m365", "run-mcp.json"))).And.NotContain("linkedin");
    }

    [Fact]
    public async Task Brief_RunMcpConfigPresentDuringRun_OnLinux0600()
    {
        // Arrange
        string? mode = null;
        var model = _run.Model(_ => "m365-brief-mail-ok", act: (request, _, _) =>
            mode = OperatingSystem.IsWindows() ? "n/a" : Convert.ToString((int)File.GetUnixFileMode(request.McpConfig!) & 0x1FF, 8));

        // Act
        var (exit, console) = await BriefAsync(model, TestContext.Current.CancellationToken);

        // Assert
        exit.Should().Be(0, console.Stderr + console.Stdout);
        mode.Should().Be(OperatingSystem.IsWindows() ? "n/a" : "600");
    }

    [Theory]
    [InlineData("""{"mcpServers":{"linkedin":{"type":"stdio","command":"zyggy","args":["linkedin","mcp-server"]}}}""")]
    [InlineData(null)]
    public async Task Brief_McpJsonWithoutM365_ExitThreeNoModelRun(string? mcpJson)
    {
        // Arrange
        var path = Path.Combine(_run.Fixture.Checkout, ".mcp.json");
        if (mcpJson is null)
        {
            File.Delete(path);
        }
        else
        {
            File.WriteAllText(path, mcpJson);
        }

        var model = _run.Model(_ => "m365-brief-mail-ok");

        // Act
        var (exit, console) = await BriefAsync(model, TestContext.Current.CancellationToken);

        // Assert
        exit.Should().Be(3);
        console.Stderr.Should().Contain($"configuration error: {path}: no m365 server");
        model.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Brief_AttachmentParseFalse_StdinAttachmentsOff()
    {
        // Arrange: spec 35 AC-29 — an instance that turns attachment parsing off
        var path = Path.Combine(_run.Fixture.InstanceDirectory, "m365.json");
        var config = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        config["brief"]!["attachment_parse"] = false;
        File.WriteAllText(path, config.ToJsonString());
        var model = _run.Model(_ => "m365-brief-mail-ok");

        // Act
        var (exit, console) = await BriefAsync(model, TestContext.Current.CancellationToken);

        // Assert
        exit.Should().Be(0, console.Stderr + console.Stdout);
        var stdin = Encoding.UTF8.GetString(File.ReadAllBytes(model.StdinCapture(0)));
        stdin.Should().StartWith("/morning-brief alice@acme.example AQMkInbox0001 attachments=off b!onedrive0001 ");
    }

    [Fact]
    public async Task Brief_MailJsonPresentDuringRun0600()
    {
        // Arrange: the acting runner reads mail.json right after the fake ran, before the binary removes the run directory
        byte[]? mailJson = null;
        UnixFileMode? mode = null;
        var model = _run.Model(_ => "m365-brief-mail-ok", act: (request, _, _) =>
        {
            var path = Path.Combine(request.Environment["ZYGGY_M365_RUN_DIR"], "mail.json");
            mailJson = File.Exists(path) ? File.ReadAllBytes(path) : null;
            mode = OperatingSystem.IsLinux() && mailJson is not null ? File.GetUnixFileMode(path) : null;
        });

        // Act
        var (exit, console) = await BriefAsync(model, TestContext.Current.CancellationToken);

        // Assert
        exit.Should().Be(0, console.Stderr + console.Stdout);
        mailJson.Should().NotBeNull();
        var input = JsonDocument.Parse(mailJson!).RootElement;
        input.GetProperty("watermark").GetString().Should().Be("2026-10-06T04:30:00Z");
        input.GetProperty("mail").EnumerateArray().Select(m => m.GetProperty("id").GetString()).Should().Equal("m10", "m11", "m12", "m13");
        input.GetProperty("mail")[1].GetProperty("answered").GetString().Should().NotBeNull("m11's conversation has a later sent mail");
        if (OperatingSystem.IsLinux())
        {
            mode.Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        _run.RunDirectories.Should().BeEmpty();
    }

    [Theory]
    [InlineData("m365-brief-mail-invalid")]
    [InlineData("m365-brief-mail-noclass")]
    public async Task Brief_InvalidOutput_ExitSixNoBriefFilesWatermarkUnchanged(string scenario)
    {
        // Arrange
        var model = _run.Model(_ => scenario);

        // Act
        var (exit, console) = await BriefAsync(model, TestContext.Current.CancellationToken);

        // Assert
        exit.Should().Be(6, console.Stderr + console.Stdout);
        console.Stderr.Should().Contain("m365-brief: claude run returned no valid brief (").And.Contain("runbook 13 \"Model run failed\"");
        File.Exists(Markdown).Should().BeFalse();
        File.Exists(Sidecar).Should().BeFalse();
        File.Exists(Path.Combine(_run.State, "brief-2026-10-06.json")).Should().BeFalse();
        File.ReadAllText(Path.Combine(_run.State, "mail-watermark")).TrimEnd('\n').Should().Be("2026-10-06T04:30:00Z");
        Directory.Exists(Path.Combine(_run.Fixture.MemoryRoot, "acme", "alice", "inbox")).Should().BeFalse();
        _run.RunDirectories.Should().BeEmpty();
    }

    [Fact]
    public async Task Brief_FlaggedText_ExitFiveFirstLineFlagged()
    {
        // Arrange: m11's summary carries an e-mail address
        var model = _run.Model(_ => "m365-brief-mail-flagged");

        // Act
        var (exit, console) = await BriefAsync(model, TestContext.Current.CancellationToken);

        // Assert
        exit.Should().Be(5, console.Stderr + console.Stdout);
        console.Stdout.Should().Contain(", audit FLAGGED, exit 5");
        var lines = File.ReadAllText(Markdown).Split('\n');
        lines[0].Should().Be("audit FLAGGED: address withheld in summary of m11");
        lines[1].Should().StartWith("Zyggy — morning brief 2026-10-06");
        File.ReadAllText(Markdown).Should().NotContain("bob@example.org").And.Contain("[withheld: address]");
    }

    [Fact]
    public async Task Brief_Denials_InLine()
    {
        // Arrange
        var model = _run.Model(_ => "m365-brief-mail-denials");

        // Act
        var (exit, console) = await BriefAsync(model, TestContext.Current.CancellationToken);

        // Assert
        exit.Should().Be(0, console.Stderr + console.Stdout);
        console.Stdout.Should().EndWith(", audit ok, denials mcp__m365__send-shared-mailbox-mail, exit 0\n");
    }

    [Fact]
    public async Task Brief_FakeError_ExitSixNoReceipt()
    {
        // Arrange
        var model = _run.Model(_ => "error");

        // Act
        var (exit, console) = await BriefAsync(model, TestContext.Current.CancellationToken);

        // Assert
        exit.Should().Be(6);
        console.Stderr.Should().Contain("m365-brief: claude run failed (error_during_execution)");
        File.Exists(Path.Combine(_run.State, "brief-2026-10-06.json")).Should().BeFalse();
        _run.RunDirectories.Should().BeEmpty();
    }

    [Fact]
    public async Task Brief_Cancelled_NoProcessRunDirRemovedExit143()
    {
        // Arrange
        var model = _run.Model(_ => "m365-brief-mail-ok", _ => 30000);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var stopper = M365RunHarness.CancelWhenStartedAsync(model, 0, cancel);

        // Act
        var (exit, _) = await BriefAsync(model, cancel.Token);
        await stopper;

        // Assert
        exit.Should().Be(143);
        ProcessProbe.ProcessesWithWorkingDirectory(_run.Fixture.Checkout).Should().Be(0);
        _run.RunDirectories.Should().BeEmpty();
        File.Exists(Markdown).Should().BeFalse();
        File.Exists(Path.Combine(_run.State, "brief-2026-10-06.json")).Should().BeFalse();
    }

    // `zyggy brief show [--full] 2026-10-06` from the binary, over the brief the run wrote.
    private async Task<ZyggyRun> ShowAsync(params string[] options)
    {
        var env = _run.Fixture.Env();
        var run = await ZyggyCli.RunAsync(["brief", "show", .. options, "2026-10-06"], env, null, _run.Fixture.Root, TestContext.Current.CancellationToken);
        run.ExitCode.Should().Be(0, run.Stderr);
        return run;
    }

    // The brief text inside the payload's fence.
    private static string Body(string stdout)
    {
        var lines = stdout.Split('\n');
        var start = Array.FindIndex(lines, l => l.StartsWith("<zyggy-brief ", StringComparison.Ordinal)) + 1;
        var end = Array.FindIndex(lines, l => l.StartsWith("</zyggy-brief>", StringComparison.Ordinal));
        return string.Join('\n', lines[start..end]);
    }
}
