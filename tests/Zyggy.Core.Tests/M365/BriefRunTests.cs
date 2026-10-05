using System.Text.Json;

using NSubstitute;

using Zyggy.Core.M365;
using Zyggy.Core.M365.Runs;
using Zyggy.Core.M365.Tools;
using Zyggy.Core.Memory;
using Zyggy.Core.Models;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.M365;

/// <summary>
/// The morning brief — <c>brief.sh</c> (spec 33 AC-30; the <c>brief:</c> bats cases at unit level): pre-flight, one model run (faked),
/// the result checks, the audit, one memory line, the <c>brief.jsonl</c> row and the journal line.
/// </summary>
public sealed class BriefRunTests : IDisposable
{
    private const string Midnight = "createdDateTime ge 2026-09-29T22";

    private readonly GraphFixture _graph = new();
    private readonly MemoryTree _tree = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zyggy-ut", Guid.NewGuid().ToString("N"));
    private readonly IModelRunner _model = Substitute.For<IModelRunner>();
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

        // brief_setup: no brief Draft yet today, m1 recorded as replied, the model succeeds
        _graph.Stub.Once("GET", Midnight, 200, """{"value":[]}""");
        Directory.CreateDirectory(State);
        File.WriteAllText(Path.Combine(State, "replied-2026-09-30.ids"), "m1\n");
        Result("claude-result-ok.json");
    }

    private string State => Path.Combine(_root, "state", "m365");

    private string DownloadRoot => Path.Combine(_root, "home", ".cache", "zyggy-m365-downloads");

    public void Dispose()
    {
        _graph.Stub.Violations.Should().BeEmpty();
        _graph.Dispose();
        _tree.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Run_HappyPath_LastLineByteEqualsJournalOk()
    {
        // Act
        var outcome = await RunAsync();

        // Assert
        outcome.Exit.Should().Be(0, string.Join('\n', outcome.StderrLines));
        outcome.StdoutLines[^1].Should().Be(File.ReadAllText(M365Run.Golden("expected", "m365-journal-ok.txt")).TrimEnd('\n'));
        outcome.StderrLines.Should().Contain("key: file");
        await _model.Received(1).RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>());
        var row = JsonDocument.Parse(File.ReadAllLines(Path.Combine(State, "brief.jsonl")).Single()).RootElement;
        row.EnumerateObject().Select(p => p.Name).Should().Equal("date", "ts", "mail", "files", "replies", "suggestions", "facts", "turns", "cost", "audit", "denials", "key", "exit");
        row.GetProperty("suggestions").GetInt32().Should().Be(2);
        row.GetProperty("cost").GetRawText().Should().Be("0.42");
        row.GetProperty("key").GetString().Should().Be("file");
        var receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(State, "brief-2026-09-30.json"))).RootElement;
        receipt.GetProperty("audit").GetString().Should().Be("ok");
        receipt.GetProperty("window_start").GetString().Should().Be("2026-09-30T10:00:00Z");
        File.ReadAllLines(_tree.Full("inbox/remember-2026-09-30.md")).Should().Contain(
            "- [observed] 2026-09-30 [m365-brief 2026-09-30]: Morning brief 2026-09-30 left as a Draft: mail 3, files 1, replies 1, suggestions 2, facts 4, audit ok");
        Directory.EnumerateDirectories(DownloadRoot).Should().BeEmpty();
    }

    [Fact]
    public async Task Run_Request_PromptAndListsExact()
    {
        // Arrange
        ModelRunRequest? request = null;
        string? runDirectoryDuringRun = null;
        _model.RunAsync(Arg.Do<ModelRunRequest>(r =>
        {
            request = r;
            runDirectoryDuringRun = Directory.Exists(r.Environment["ZYGGY_M365_RUN_DIR"]) ? r.Environment["ZYGGY_M365_RUN_DIR"] : null;
        }), Arg.Any<CancellationToken>()).Returns(Build("claude-result-ok.json"));

        // Act
        await RunAsync();

        // Assert
        var runDirectory = request!.Environment["ZYGGY_M365_RUN_DIR"];
        runDirectoryDuringRun.Should().Be(runDirectory);
        Path.GetDirectoryName(runDirectory).Should().Be(DownloadRoot);
        Path.GetFileName(runDirectory).Should().MatchRegex("^zyggy-m365-brief-2026-09-30\\.[A-Za-z0-9]{6}$");
        request.Prompt.Should().Be($"/morning-brief alice@acme.example AQMkInbox0001 b!onedrive0001 b!ops0001 b!opsarchive0001 {runDirectory}");
        request.AllowedTools.Should().Equal(File.ReadAllLines(M365Run.Golden("run-lists", "brief-allow.txt")));
        request.DisallowedTools.Should().Equal(File.ReadAllLines(M365Run.Golden("run-lists", "brief-deny.txt")));
        request.MaxTurns.Should().Be(40);
        request.MaxBudgetUsd.Should().Be(3.0m);
        request.Environment["ZYGGY_HOOKS"].Should().Be("off");
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
        outcome.Should().BeEquivalentTo(new RunOutcome(0, ["brief 2026-09-30: already created"], ["key: file"]));
        await _model.DidNotReceive().RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Run_BriefDraftOfTodayInGraph_AlreadyCreated()
    {
        // Arrange: no receipt, but the midnight query finds today's brief Draft (the golden route: drafts-ok's d0)
        _graph.Stub.Requests.Clear();
        using var graph = new GraphFixture();
        var run = new BriefRun(_session with { Configuration = graph.Configuration }, _partition, graph.Reader, _model, graph.Clock);

        // Act
        var outcome = await run.RunAsync(CancellationToken.None);

        // Assert
        outcome.StdoutLines.Should().Equal("brief 2026-09-30: already created");
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
        row.GetProperty("exit").GetInt32().Should().Be(6);
    }

    [Fact]
    public async Task Run_IsError_ExitSixNoReceiptNoMemoryLineRunDirRemoved()
    {
        // Arrange
        Result("claude-result-error.json");

        // Act
        var outcome = await RunAsync();

        // Assert
        outcome.Exit.Should().Be(6);
        outcome.StderrLines[^1].Should().Be("m365-brief: claude run failed (error_during_execution) — runbook 13 \"Model run failed\"");
        File.Exists(Path.Combine(State, "brief-2026-09-30.json")).Should().BeFalse();
        File.Exists(_tree.Full("inbox/remember-2026-09-30.md")).Should().BeFalse();
        Directory.EnumerateDirectories(DownloadRoot).Should().BeEmpty();
    }

    [Fact]
    public async Task Run_OverBudget_ExitSixCapMessage()
    {
        // Arrange
        Result("claude-result-over-budget.json");

        // Act
        var outcome = await RunAsync();

        // Assert
        outcome.Exit.Should().Be(6);
        outcome.StderrLines[^1].Should().Be("m365-brief: claude run over the cap (cost 3.51 > budget 3.0, turns 38 of 40) — runbook 13 \"Model run failed\"");
    }

    [Fact]
    public async Task Run_NoResult_ExitSixNoJson()
    {
        // Arrange
        _model.RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>()).Returns(
            new ModelRunResult(ModelRunOutcome.Failed, null, ModelFailureDetail.NoResult, null, null, null, null, TimeSpan.Zero, null, null, null, 1, 0));

        // Act
        var outcome = await RunAsync();

        // Assert
        outcome.StderrLines[^1].Should().Be("m365-brief: claude returned no JSON result (exit 1) — runbook 13 \"Model run failed\"");
    }

    [Fact]
    public async Task Run_Denials_ToolNamesInLineAndRow()
    {
        // Arrange
        Result("claude-result-denials.json");

        // Act
        var outcome = await RunAsync();

        // Assert
        outcome.Exit.Should().Be(0);
        outcome.StdoutLines[^1].Should().Be(
            "brief 2026-09-30: mail 3, files 1, replies 1, suggestions 2, facts 4, turns 12, cost 0.42, audit ok, denials mcp__m365__send-shared-mailbox-mail, exit 0");
        JsonDocument.Parse(File.ReadAllLines(Path.Combine(State, "brief.jsonl"))[^1]).RootElement.GetProperty("denials")[0].GetString()
            .Should().Be("mcp__m365__send-shared-mailbox-mail");
    }

    [Fact]
    public async Task Run_AuditFlagged_ExitFiveLineMatchesJournalFlagged()
    {
        // Arrange
        _graph.Stub.Once("GET", "createdDateTime ge 2026-09-30T10", 200, File.ReadAllText(StubGraphHandler.GraphFixture("drafts-flagged.json")));

        // Act
        var outcome = await RunAsync();

        // Assert
        outcome.Exit.Should().Be(5);
        outcome.StdoutLines[^1].Should().Be(File.ReadAllText(M365Run.Golden("expected", "m365-journal-flagged.txt")).TrimEnd('\n'));
        File.ReadAllText(_tree.Full("inbox/remember-2026-09-30.md")).Should().Contain("suggestions 2, facts 4, audit FLAGGED");
    }

    [Fact]
    public async Task Run_Cancelled_RunDirRemovedNoReceiptInterrupted143()
    {
        // Arrange
        using var cancel = new CancellationTokenSource();
        _model.RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            await cancel.CancelAsync();
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return Build("claude-result-ok.json");
        });

        // Act
        var outcome = await new BriefRun(_session, _partition, _graph.Reader, _model, _graph.Clock).RunAsync(cancel.Token);

        // Assert
        outcome.Exit.Should().Be(143);
        Directory.EnumerateDirectories(DownloadRoot).Should().BeEmpty();
        File.Exists(Path.Combine(State, "brief-2026-09-30.json")).Should().BeFalse();
    }

    private ModelRunResult Result(string fixture)
    {
        var result = Build(fixture);
        _model.RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>()).Returns(result);
        return result;
    }

    private static ModelRunResult Build(string fixture)
    {
        var json = JsonDocument.Parse(File.ReadAllText(M365Run.Golden("fixtures", fixture))).RootElement;
        var isError = json.GetProperty("is_error").GetBoolean();
        var result = new ModelRunResult(
            isError ? ModelRunOutcome.Failed : ModelRunOutcome.Succeeded,
            null,
            isError ? json.GetProperty("subtype").GetString() : null,
            json.GetProperty("result").GetString(),
            null,
            json.GetProperty("total_cost_usd").GetDecimal(),
            json.GetProperty("num_turns").GetInt32(),
            TimeSpan.FromSeconds(1),
            null,
            null,
            null,
            0,
            json.GetProperty("permission_denials").GetArrayLength())
        {
            PermissionDenialTools = [.. json.GetProperty("permission_denials").EnumerateArray().Select(d => d.GetProperty("tool_name").GetString()!)],
        };
        return result;
    }

    private Task<RunOutcome> RunAsync() => new BriefRun(_session, _partition, _graph.Reader, _model, _graph.Clock).RunAsync(CancellationToken.None);
}
