using System.Text.Json;

using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Zyggy.Core.M365;
using Zyggy.Core.M365.Runs;
using Zyggy.Core.M365.Tools;
using Zyggy.Core.Memory;
using Zyggy.Core.Models;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.M365;

/// <summary>
/// The morning brief run of spec 35 (Step 6; AC-20, AC-40..AC-43): the pre-pass, one mail run with structured output (faked), the answer
/// checked, the audit without a brief Draft, the watermark, one memory line, the sidecar and the brief file, retention, the
/// <c>brief.jsonl</c> row and the journal line. Tuesday 2026-10-06, 10:30 Brussels; the stub's Inbox holds m10..m13.
/// </summary>
public sealed class BriefRunTests : IDisposable
{
    private const string DraftsRoute = "mailFolders/drafts/messages\\?\\$filter";
    private const string ReplyDraft = """{"id":"d1","subject":"RE: Invoice 2026-41","toRecipients":[{"emailAddress":{"name":"Carol","address":"carol@example.org"}}],"ccRecipients":[],"bccRecipients":[],"conversationId":"c1","createdDateTime":"2026-10-06T08:31:00Z","changeKey":"CK1","isDraft":true,"body":{"contentType":"text","content":"Dear Carol, I will call tomorrow.\n"}}""";

    private readonly GraphFixture _graph = new();
    private readonly MemoryTree _tree = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zyggy-ut", Guid.NewGuid().ToString("N"));
    private readonly IModelRunner _model = Substitute.For<IModelRunner>();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 6, 8, 30, 0, TimeSpan.Zero));
    private readonly M365Session _session;
    private readonly M365ToolPartition _partition;

    public BriefRunTests()
    {
        var tools = Path.Combine(_root, "checkout", ".claude", "skills", "m365", "tools");
        Directory.CreateDirectory(tools);
        foreach (var file in Directory.EnumerateFiles(M365Run.Golden("tools")))
        {
            File.Copy(file, Path.Combine(tools, Path.GetFileName(file)));
        }

        Directory.CreateDirectory(Path.Combine(_root, "checkout", "instance"));
        var instance = M365Environment.Load(new Dictionary<string, string?>
        {
            ["ZYGGY_INSTANCE_DIR"] = Path.Combine(_root, "checkout", "instance"),
            ["ZYGGY_STATE_DIR"] = Path.Combine(_root, "state"),
            ["HOME"] = Path.Combine(_root, "home"),
        }).Environment!;
        var brussels = TimeZoneInfo.CreateCustomTimeZone("Europe/Brussels", TimeSpan.FromHours(2), "Test/Brussels", "Test/Brussels");
        var patterns = SecretPatterns.Load(Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt")).Patterns!;
        _session = new M365Session(_tree.Paths, brussels, instance, _graph.Configuration, null, patterns);
        _partition = M365ToolPartition.Load(instance.Checkout).Partition!;

        // The watermark of the night, m1 recorded as replied (the stub's d1 reply Draft answers it), no brief Draft, the model answers in the schema
        Directory.CreateDirectory(State);
        File.WriteAllText(Path.Combine(State, "mail-watermark"), "2026-10-06T04:30:00Z\n");
        File.WriteAllText(Path.Combine(State, "replied-2026-10-06.ids"), "m1\n");
        // yesterday's receipt names the reply Draft d5, the pre-pass's discard candidate (the golden mail-input.json)
        File.WriteAllText(Path.Combine(State, "brief-2026-10-05.json"), """{"date":"2026-10-05","window_start":"2026-10-05T04:30:00Z","drafts":[{"id":"d5","kind":"reply","subject":"RE: Old thread","recipients":[]}],"replied_ids":[],"audit":"ok","reasons":[]}""");
        _graph.Stub.Once("GET", DraftsRoute, 200, "{\"value\":[" + ReplyDraft + "]}");
        Result("mail-output-ok.json");
    }

    private string State => Path.Combine(_root, "state", "m365");

    private string BriefDirectory => Path.Combine(_root, "state", "brief");

    private string Markdown => Path.Combine(BriefDirectory, "brief-2026-10-06.md");

    private string Sidecar => Path.Combine(BriefDirectory, "brief-2026-10-06.json");

    private string DownloadRoot => Path.Combine(_root, "home", ".cache", "zyggy-m365-downloads");

    public void Dispose()
    {
        _graph.Stub.Violations.Should().BeEmpty();
        _graph.Dispose();
        _tree.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Run_Weekday_WritesSidecarThenBriefWatermarkMemoryRowAndJournalLine()
    {
        // Act
        var outcome = await RunAsync();

        // Assert
        outcome.Exit.Should().Be(0, string.Join('\n', outcome.StderrLines));
        outcome.StdoutLines[^1].Should().Be(File.ReadAllText(M365Run.Golden("expected", "journal-weekday.txt")).TrimEnd('\n'));
        outcome.StderrLines.Should().Contain("key: file");
        await _model.Received(1).RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>());

        var markdown = File.ReadAllText(Markdown);
        markdown.Split('\n')[0].Should().Be("Zyggy — morning brief 2026-10-06 (4 new mails since 2026-10-06T04:30:00Z: 1 urgent, 3 important, 0 other; 1 changed file)");
        markdown.Should().Contain("Z1.").And.Contain("Z2.").And.NotContain("audit FLAGGED");
        var sidecar = JsonDocument.Parse(File.ReadAllText(Sidecar)).RootElement;
        sidecar.GetProperty("audit").GetString().Should().Be("ok");
        sidecar.GetProperty("items").GetArrayLength().Should().Be(3);
        sidecar.GetProperty("page_exceeded").GetBoolean().Should().BeFalse();

        File.ReadAllText(Path.Combine(State, "mail-watermark")).TrimEnd('\n').Should().Be("2026-10-06T08:15:00Z", "the newest pre-pass mail, set by the binary");
        var row = JsonDocument.Parse(File.ReadAllLines(Path.Combine(State, "brief.jsonl")).Single()).RootElement;
        row.EnumerateObject().Select(p => p.Name).Should().Equal(
            "date", "ts", "mail", "files", "replies", "z", "you", "ideas", "facts", "turns", "cost", "audit", "denials", "key", "exit",
            "mode", "z_dropped", "ideas_dropped", "ideas_turns", "ideas_cost", "ideas_exit", "counts", "page_exceeded");
        File.ReadAllText(Path.Combine(State, "brief.jsonl")).TrimEnd('\n').Should().Be(File.ReadAllText(M365Run.Golden("expected", "brief-jsonl-weekday.json")).TrimEnd('\n'));
        var receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(State, "brief-2026-10-06.json"))).RootElement;
        receipt.GetProperty("audit").GetString().Should().Be("ok");
        receipt.GetProperty("drafts").GetArrayLength().Should().Be(1, "the reply Draft; no brief Draft");
        File.ReadAllLines(_tree.Full("inbox/remember-2026-10-06.md")).Should().Contain(
            "- [observed] 2026-10-06 [m365-brief 2026-10-06]: Morning brief 2026-10-06 written: mail 4 (1 urgent, 3 important, 0 other), files 1, replies 1, z 3, you 1, ideas 0, facts 2, audit ok");
        Directory.EnumerateDirectories(DownloadRoot).Should().BeEmpty();
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(Markdown).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.GetUnixFileMode(BriefDirectory).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task Run_Request_PromptListsSchemaIsolationAndMailJson()
    {
        // Arrange
        ModelRunRequest? request = null;
        byte[]? mailJson = null;
        _model.RunAsync(Arg.Do<ModelRunRequest>(r =>
        {
            request = r;
            var mail = Path.Combine(r.Environment["ZYGGY_M365_RUN_DIR"], "mail.json");
            mailJson = File.Exists(mail) ? File.ReadAllBytes(mail) : null;
        }), Arg.Any<CancellationToken>()).Returns(Build("mail-output-ok.json"));

        // Act
        await RunAsync();

        // Assert
        var runDirectory = request!.Environment["ZYGGY_M365_RUN_DIR"];
        Path.GetDirectoryName(runDirectory).Should().Be(DownloadRoot);
        Path.GetFileName(runDirectory).Should().MatchRegex("^zyggy-m365-brief-2026-10-06\\.[A-Za-z0-9]{6}$");
        request.Prompt.Should().Be($"/morning-brief alice@acme.example AQMkInbox0001 attachments=on b!onedrive0001 b!ops0001 b!opsarchive0001 {runDirectory}");
        request.AllowedTools.Should().Equal([.. File.ReadAllLines(M365Run.Golden("run-lists", "brief-allow.txt")), $"Read({runDirectory}/**)"]);
        request.DisallowedTools.Should().Equal([.. File.ReadAllLines(M365Run.Golden("run-lists", "brief-deny.txt")), $"Read(//{Path.Join(_session.Instance.Checkout, "memory").Replace('\\', '/')}/**)"]);
        request.JsonSchema.Should().Be(new Core.Brief.BriefPrompts().MailSchema);
        request.Isolation.Should().Be(ModelSessionIsolation.NoAutoMemory);
        request.Timeout.Should().Be(TimeSpan.FromMinutes(30));
        request.MaxTurns.Should().Be(40);
        request.MaxBudgetUsd.Should().Be(3.0m);
        mailJson.Should().Equal(File.ReadAllBytes(Path.Combine(Golden.Directory, "brief", "mail-input.json")), "the pre-pass list is the mail run's only mail input");
    }

    [Fact]
    public async Task Run_SecondRunSameDate_AlreadyCreatedNoModelCall()
    {
        // Arrange
        await RunAsync();
        _model.ClearReceivedCalls();

        // Act
        var outcome = await RunAsync();

        // Assert
        outcome.Should().BeEquivalentTo(new RunOutcome(0, ["brief 2026-10-06: already created"], ["key: file"]));
        await _model.DidNotReceive().RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Run_ReceiptWithoutBriefFile_AlreadyCreatedNamesTheRunbook()
    {
        // Arrange: a run that wrote its receipt and then lost the brief file
        File.WriteAllText(Path.Combine(State, "brief-2026-10-06.json"), """{"date":"2026-10-06","drafts":[],"audit":"ok","reasons":[]}""");

        // Act
        var outcome = await RunAsync();

        // Assert
        outcome.StdoutLines.Should().Equal("brief 2026-10-06: already created (brief file missing — runbook 13 \"Brief run failed\")");
        await _model.DidNotReceive().RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Run_InvalidClient_ExitSixBeforeModelNoRunDir()
    {
        // Arrange
        _graph.Stub.Once("POST", "/oauth2/v2\\.0/token$", 400, File.ReadAllText(StubGraphHandler.GraphFixture("token-invalid-client.json")));

        // Act
        var outcome = await RunAsync();

        // Assert
        outcome.Exit.Should().Be(6);
        outcome.StdoutLines.Should().BeEmpty();
        outcome.StderrLines.Should().Equal("key: file", "m365-brief: auth failed (invalid_client) — runbook 13 \"Certificate rejected\"");
        await _model.DidNotReceive().RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>());
        Directory.Exists(DownloadRoot).Should().BeFalse();
        var row = JsonDocument.Parse(File.ReadAllLines(Path.Combine(State, "brief.jsonl"))[^1]).RootElement;
        row.EnumerateObject().Select(p => p.Name).Should().Equal("date", "ts", "exit", "error");
        File.ReadAllText(Path.Combine(State, "mail-watermark")).TrimEnd('\n').Should().Be("2026-10-06T04:30:00Z", "a failed run leaves the watermark");
    }

    [Fact]
    public async Task Run_ModelError_ExitSixNoFilesNoMemoryLineRunDirRemoved()
    {
        // Arrange
        _model.RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>()).Returns(
            new ModelRunResult(ModelRunOutcome.Failed, null, "error_during_execution", null, null, 0.1m, 3, TimeSpan.Zero, null, null, null, 1, 0));

        // Act
        var outcome = await RunAsync();

        // Assert
        outcome.Exit.Should().Be(6);
        outcome.StderrLines[^1].Should().Be("m365-brief: claude run failed (error_during_execution) — runbook 13 \"Model run failed\"");
        File.Exists(Markdown).Should().BeFalse();
        File.Exists(Sidecar).Should().BeFalse();
        File.Exists(Path.Combine(State, "brief-2026-10-06.json")).Should().BeFalse();
        File.Exists(_tree.Full("inbox/remember-2026-10-06.md")).Should().BeFalse();
        File.ReadAllText(Path.Combine(State, "mail-watermark")).TrimEnd('\n').Should().Be("2026-10-06T04:30:00Z");
        Directory.EnumerateDirectories(DownloadRoot).Should().BeEmpty();
    }

    [Fact]
    public async Task Run_AnswerNotInTheSchema_ExitSixNamesTheRejection()
    {
        // Arrange: a result without structured output
        _model.RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>()).Returns(
            new ModelRunResult(ModelRunOutcome.Succeeded, null, null, "All done.", null, 0.42m, 12, TimeSpan.Zero, null, null, null, 0, 0));

        // Act
        var outcome = await RunAsync();

        // Assert
        outcome.Exit.Should().Be(6);
        outcome.StderrLines[^1].Should().StartWith("m365-brief: claude run returned no valid brief (").And.EndWith(") — runbook 13 \"Model run failed\"");
        File.Exists(Markdown).Should().BeFalse();
        var row = JsonDocument.Parse(File.ReadAllLines(Path.Combine(State, "brief.jsonl"))[^1]).RootElement;
        row.GetProperty("exit").GetInt32().Should().Be(6);
    }

    [Fact]
    public async Task Run_OverBudget_ExitSixCapMessage()
    {
        // Arrange
        Result("mail-output-ok.json", cost: 3.51m, turns: 38);

        // Act
        var outcome = await RunAsync();

        // Assert
        outcome.Exit.Should().Be(6);
        outcome.StderrLines[^1].Should().Be("m365-brief: claude run over the cap (cost 3.51 > budget 3.0, turns 38 of 40) — runbook 13 \"Model run failed\"");
    }

    [Fact]
    public async Task Run_BriefDraftInDrafts_AuditFlaggedExitFiveBriefStillWritten()
    {
        // Arrange: the default drafts route holds d0, a brief-subject Draft (33's shape) — a violation in the session mode
        _graph.Stub.Requests.Clear();
        using var graph = new GraphFixture();
        File.WriteAllText(Path.Combine(State, "replied-2026-10-06.ids"), "m1\n");

        // Act
        var outcome = await new BriefRun(_session with { Configuration = graph.Configuration }, _partition, graph.Reader, _model, _clock).RunAsync(CancellationToken.None);

        // Assert
        outcome.Exit.Should().Be(5, string.Join('\n', outcome.StderrLines));
        outcome.StdoutLines[^1].Should().Be(
            "brief 2026-10-06: mail 4 (1 urgent, 3 important, 0 other), files 1, replies 1, z 3, you 1, ideas 0, facts 2, turns 12, cost 0.42, audit FLAGGED, exit 5");
        File.ReadAllText(Markdown).Split('\n')[0].Should().Be("audit FLAGGED: 1 brief draft (the brief is shown in the session)");
        JsonDocument.Parse(File.ReadAllText(Sidecar)).RootElement.GetProperty("audit").GetString().Should().Be("flagged");
        File.ReadAllText(_tree.Full("inbox/remember-2026-10-06.md")).Should().Contain("facts 2, audit FLAGGED");
        JsonDocument.Parse(File.ReadAllLines(Path.Combine(State, "brief.jsonl"))[^1]).RootElement.GetProperty("audit").GetString().Should().Be("FLAGGED");
    }

    [Fact]
    public async Task Run_Denials_ToolNamesInLineAndRow()
    {
        // Arrange
        Result("mail-output-ok.json", denials: ["mcp__m365__send-shared-mailbox-mail"]);

        // Act
        var outcome = await RunAsync();

        // Assert
        outcome.Exit.Should().Be(0);
        outcome.StdoutLines[^1].Should().EndWith(", audit ok, denials mcp__m365__send-shared-mailbox-mail, exit 0");
        JsonDocument.Parse(File.ReadAllLines(Path.Combine(State, "brief.jsonl"))[^1]).RootElement.GetProperty("denials")[0].GetString()
            .Should().Be("mcp__m365__send-shared-mailbox-mail");
    }

    [Fact]
    public async Task Run_Retention_OlderBriefFilesPrunedLastShownKept()
    {
        // Arrange: a brief of 15 days ago, one of 5 days ago, the last-shown file
        Directory.CreateDirectory(BriefDirectory);
        File.WriteAllText(Path.Combine(BriefDirectory, "brief-2026-09-21.md"), "old\n");
        File.WriteAllText(Path.Combine(BriefDirectory, "brief-2026-09-21.json"), "{}\n");
        File.WriteAllText(Path.Combine(BriefDirectory, "brief-2026-10-01.md"), "recent\n");
        File.WriteAllText(Path.Combine(BriefDirectory, "last-shown"), "2026-10-01\n");

        // Act
        var outcome = await RunAsync();

        // Assert
        outcome.Exit.Should().Be(0, string.Join('\n', outcome.StderrLines));
        Directory.EnumerateFiles(BriefDirectory).Select(Path.GetFileName).Order(StringComparer.Ordinal)
            .Should().Equal("brief-2026-10-01.md", "brief-2026-10-06.json", "brief-2026-10-06.md", "last-shown");
    }

    [Fact]
    public async Task Run_Cancelled_RunDirRemovedNoFilesInterrupted143()
    {
        // Arrange
        using var cancel = new CancellationTokenSource();
        _model.RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            await cancel.CancelAsync();
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return Build("mail-output-ok.json");
        });

        // Act
        var outcome = await new BriefRun(_session, _partition, _graph.Reader, _model, _clock).RunAsync(cancel.Token);

        // Assert
        outcome.Exit.Should().Be(143);
        Directory.EnumerateDirectories(DownloadRoot).Should().BeEmpty();
        File.Exists(Markdown).Should().BeFalse();
        File.Exists(Path.Combine(State, "brief-2026-10-06.json")).Should().BeFalse();
    }

    private ModelRunResult Result(string fixture, decimal cost = 0.42m, int turns = 12, string[]? denials = null)
    {
        var result = Build(fixture, cost, turns, denials);
        _model.RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>()).Returns(result);
        return result;
    }

    // The model's structured answer (the --json-schema result), as ClaudeCodeCliRunner surfaces it.
    private static ModelRunResult Build(string fixture, decimal cost = 0.42m, int turns = 12, string[]? denials = null)
    {
        var output = JsonDocument.Parse(File.ReadAllText(M365Run.Golden("fixtures", fixture))).RootElement.Clone();
        return new ModelRunResult(ModelRunOutcome.Succeeded, null, null, null, output, cost, turns, TimeSpan.FromSeconds(1), null, null, null, 0, denials?.Length ?? 0)
        {
            PermissionDenialTools = denials ?? [],
        };
    }

    private Task<RunOutcome> RunAsync() => new BriefRun(_session, _partition, _graph.Reader, _model, _clock).RunAsync(CancellationToken.None);
}
