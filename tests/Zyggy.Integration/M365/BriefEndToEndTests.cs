using System.Text;
using System.Text.Json;

using Zyggy.Core.Models;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.M365;

/// <summary>
/// Spec 33 AC-30 (I, in process): <c>zyggy m365 brief</c> through the real process runner and <c>tools/fake-claude</c> — the captured
/// argument vector carries the deny list, the prompt arrives on stdin, a stop leaves no model process and no run directory.
/// </summary>
public sealed class BriefEndToEndTests : IDisposable
{
    private readonly M365RunHarness _run = new();

    // brief_setup: no brief Draft yet today; the audit afterwards reads the fixture's drafts.
    public BriefEndToEndTests() => _run.Graph.Once("GET", "createdDateTime ge 2026-09-", 200, """{"value":[]}""");

    public void Dispose() => _run.Dispose();

    // What the model does through `zyggy m365 state` during a brief: it records the message it drafted a reply to.
    private void RecordsReply(ModelRunRequest request, ModelRunResult result, int call) =>
        File.WriteAllText(Path.Combine(_run.State, "replied-2026-09-30.ids"), "m1\n");

    private static string[] Lines(string file) => File.ReadAllLines(M365InstanceFixture.Golden("m365", "run-lists", file));

    [Fact]
    public async Task Brief_HappyPath_JournalLineReceiptBriefJsonlMemoryLine()
    {
        // Arrange
        var model = _run.Model(_ => "m365-brief-ok", act: RecordsReply);

        // Act
        var (exit, console) = await _run.RunAsync(model, ["brief"], TestContext.Current.CancellationToken);

        // Assert
        exit.Should().Be(0, console.Stderr + console.Stdout);
        console.Stdout.Should().EndWith(File.ReadAllText(M365InstanceFixture.Golden("m365", "expected", "m365-journal-ok.txt")));
        console.Stderr.Should().Be("key: file\n");
        JsonDocument.Parse(File.ReadAllText(Path.Combine(_run.State, "brief-2026-09-30.json"))).RootElement.GetProperty("audit").GetString().Should().Be("ok");
        JsonDocument.Parse(File.ReadAllLines(Path.Combine(_run.State, "brief.jsonl")).Single()).RootElement.GetProperty("cost").GetRawText().Should().Be("0.42");
        File.ReadAllLines(Path.Combine(_run.Fixture.MemoryRoot, "acme", "alice", "inbox", "remember-2026-09-30.md")).Should().Contain(
            "- [observed] 2026-09-30 [m365-brief 2026-09-30]: Morning brief 2026-09-30 left as a Draft: mail 3, files 1, replies 1, suggestions 2, facts 4, audit ok");
        _run.RunDirectories.Should().BeEmpty();
    }

    [Fact]
    public async Task Brief_CapturedArgumentsEqualHandWrittenVectorPromptOnStdin()
    {
        // Arrange
        var model = _run.Model(_ => "m365-brief-ok", act: RecordsReply);
        string[] expected =
        [
            "-p", "--output-format", "stream-json", "--verbose", "--permission-mode", "auto", "--permission-prompts", "none",
            "--no-session-persistence", "--max-turns", "40", "--max-budget-usd", "3.0",
            "--allowedTools", string.Join(',', Lines("brief-allow.txt")),
            "--disallowedTools", string.Join(',', Lines("brief-deny.txt")),
            "--strict-mcp-config", "--mcp-config", Path.Join(_run.Fixture.Checkout, ".mcp.json"),
        ];

        // Act
        var (exit, console) = await _run.RunAsync(model, ["brief"], TestContext.Current.CancellationToken);

        // Assert
        exit.Should().Be(0, console.Stderr + console.Stdout);
        var capture = FakeClaude.ReadCapture(model.ArgumentsCapture(0));
        capture.Arguments.Should().Equal(expected);
        capture.Arguments.Should().NotContain(a => a.StartsWith("/morning-brief", StringComparison.Ordinal));
        Path.GetFullPath(capture.WorkingDirectory).Should().Be(Path.GetFullPath(_run.Fixture.Checkout));
        var runDirectory = model.Requests[0].Environment["ZYGGY_M365_RUN_DIR"];
        var stdin = Encoding.UTF8.GetString(File.ReadAllBytes(model.StdinCapture(0)));
        stdin.Should().StartWith("/morning-brief alice@acme.example AQMkInbox0001 b!onedrive0001 ").And.EndWith(" " + runDirectory);
    }

    [Fact]
    public async Task Brief_Denials_InLine()
    {
        // Arrange
        var model = _run.Model(_ => "m365-brief-denials", act: RecordsReply);

        // Act
        var (exit, console) = await _run.RunAsync(model, ["brief"], TestContext.Current.CancellationToken);

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
        var (exit, console) = await _run.RunAsync(model, ["brief"], TestContext.Current.CancellationToken);

        // Assert
        exit.Should().Be(6);
        console.Stderr.Should().Contain("m365-brief: claude run failed (error_during_execution)");
        File.Exists(Path.Combine(_run.State, "brief-2026-09-30.json")).Should().BeFalse();
        _run.RunDirectories.Should().BeEmpty();
    }

    [Fact]
    public async Task Brief_CancelledWhileFakeRuns_NoProcessLeftRunDirRemovedExit143()
    {
        // Arrange
        var model = _run.Model(_ => "m365-brief-ok", _ => 30000);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var stopper = M365RunHarness.CancelWhenStartedAsync(model, 0, cancel);

        // Act
        var (exit, _) = await _run.RunAsync(model, ["brief"], cancel.Token);
        await stopper;

        // Assert
        exit.Should().Be(143);
        ProcessProbe.ProcessesWithWorkingDirectory(_run.Fixture.Checkout).Should().Be(0);
        _run.RunDirectories.Should().BeEmpty();
        File.Exists(Path.Combine(_run.State, "brief-2026-09-30.json")).Should().BeFalse();
    }
}
