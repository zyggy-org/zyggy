using System.Text;
using System.Text.Json.Nodes;

using Zyggy.Core.Models;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.M365;

/// <summary>
/// Spec 33 AC-31 and AC-33 (I, in process): <c>zyggy m365 mail-backfill</c> through the real process runner and <c>tools/fake-claude</c> —
/// the shell's checkpoint resumes, a stop mid-batch keeps the last checkpoint and the next run resumes from it.
/// </summary>
public sealed class MailBackfillEndToEndTests : IDisposable
{
    private const string Earlier = "2026-08-30T10:00:00Z";

    private readonly M365RunHarness _run = new();

    public void Dispose() => _run.Dispose();

    private string Checkpoint => Path.Combine(_run.State, "mail-backfill.json");

    private static string[] Lines(string file) => File.ReadAllLines(M365InstanceFixture.Golden("m365", "run-lists", file));

    // Per folder a batch of 25 then an empty batch; after a batch the model has moved the folder's watermark (zyggy m365 state).
    private static string BatchThenEmpty(int call) => call % 2 == 0 ? "m365-mail-batch" : "m365-mail-empty";

    private void MovesWatermark(ModelRunRequest request, ModelRunResult result, int call)
    {
        if (result.ResultText?.Contains("messages 25", StringComparison.Ordinal) == true)
        {
            File.WriteAllText(Path.Combine(_run.State, $"backfill-{request.Prompt.Split(' ')[2]}.watermark"), Earlier + "\n");
        }
    }

    [Fact]
    public async Task MailBackfill_ShellCheckpoint_PrintsResumingThenDone()
    {
        // Arrange: jq -c output of mail-backfill.sh — Inbox finished, Sent Items two batches in
        File.WriteAllText(Checkpoint,
            """{"folders":{"AQMkInbox0001":{"name":"Inbox","watermark":"2026-01-01T00:00:00Z","done":true,"batches":7,"messages":150,"facts":40,"duplicates":3,"refused":1,"turns":30,"cost":1.2},"AQMkSentItems0001":{"name":"Sent Items","watermark":"2026-07-01T00:00:00Z","done":false,"batches":2,"messages":50,"facts":12,"duplicates":0,"refused":0,"turns":8,"cost":0.3}},"total_messages":200,"total_facts":52,"total_duplicates":3,"total_refused":1,"total_batches":9,"total_turns":38,"total_cost":1.5,"started":"2026-09-20T08:00:00Z","updated":"2026-09-21T08:00:00Z"}""" + "\n");
        File.WriteAllText(Path.Combine(_run.State, "backfill-AQMkSentItems0001.watermark"), "2026-07-01T00:00:00Z\n");
        var model = _run.Model(_ => "m365-mail-empty");

        // Act
        var (exit, console) = await _run.RunAsync(model, ["mail-backfill"], TestContext.Current.CancellationToken);

        // Assert
        exit.Should().Be(0, console.Stderr);
        var stdout = console.Stdout.Split('\n');
        stdout.Should().Contain("mail-backfill: resuming folder Sent Items from 2026-07-01T00:00:00Z");
        stdout[^2].Should().StartWith("mail-backfill: done — folders 3 (excluded 3), messages 200, batches 11, ");
        model.Requests.Should().HaveCount(2).And.NotContain(r => r.Prompt.Contains("AQMkInbox0001", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MailBackfill_CancelledDuringBatch_Exit130CheckpointIntactNextRunResumes()
    {
        // Arrange: the second batch of the Inbox hangs until the stop
        var first = _run.Model(BatchThenEmpty, call => call == 1 ? 30000 : null, MovesWatermark);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var stopper = M365RunHarness.CancelWhenStartedAsync(first, 1, cancel);

        // Act
        var (stopped, _) = await _run.RunAsync(first, ["mail-backfill"], cancel.Token);
        await stopper;
        var inbox = JsonNode.Parse(File.ReadAllText(Checkpoint))!["folders"]!["AQMkInbox0001"]!;
        var (exit, console) = await _run.RunAsync(_run.Model(_ => "m365-mail-empty"), ["mail-backfill"], TestContext.Current.CancellationToken);

        // Assert
        stopped.Should().Be(130);
        inbox["batches"]!.GetValue<double>().Should().Be(1);
        inbox["done"]!.GetValue<bool>().Should().BeFalse();
        exit.Should().Be(0, console.Stderr);
        console.Stdout.Split('\n')[0].Should().Be($"mail-backfill: resuming folder Inbox from {Earlier}");
        ProcessProbe.ProcessesWithWorkingDirectory(_run.Fixture.Checkout).Should().Be(0);
    }

    [Fact]
    public async Task MailBackfill_CapturedArgs_RunMcpConfigOnlyM365_DenyIncludesLinkedIn()
    {
        // Arrange: spec 36 AC-8
        string? loaded = null;
        var model = _run.Model(BatchThenEmpty, act: (request, result, call) =>
        {
            loaded ??= File.ReadAllText(request.McpConfig!);
            MovesWatermark(request, result, call);
        });

        // Act
        var (exit, console) = await _run.RunAsync(model, ["mail-backfill", "--folder", "Inbox"], TestContext.Current.CancellationToken);

        // Assert
        exit.Should().Be(0, console.Stderr);
        Lines("mail-backfill-deny.txt").Should().Contain(["mcp__linkedin__*", "Bash(zyggy linkedin *)"]);
        FakeClaude.ReadCapture(model.ArgumentsCapture(0)).Arguments.Should().Contain(string.Join(',', Lines("mail-backfill-deny.txt")));
        loaded.Should().Be(File.ReadAllText(M365InstanceFixture.Golden("m365", "run-mcp.json")));
    }

    [Fact]
    public async Task MailBackfill_CapturedArguments_MailListsPromptOnStdin()
    {
        // Arrange
        var model = _run.Model(BatchThenEmpty, act: MovesWatermark);
        string[] expected =
        [
            "-p", "--output-format", "stream-json", "--verbose", "--permission-mode", "auto", "--permission-prompts", "none",
            "--no-session-persistence", "--max-turns", "15", "--max-budget-usd", "0.5",
            "--allowedTools", string.Join(',', Lines("mail-backfill-allow.txt")),
            "--disallowedTools", string.Join(',', Lines("mail-backfill-deny.txt")),
            "--model", "sonnet",
            "--strict-mcp-config", "--mcp-config", Path.Join(_run.Fixture.StateDirectory, "m365", "run-mcp.json"),
        ];

        // Act
        var (exit, console) = await _run.RunAsync(model, ["mail-backfill", "--folder", "Inbox"], TestContext.Current.CancellationToken);

        // Assert
        exit.Should().Be(0, console.Stderr);
        FakeClaude.ReadCapture(model.ArgumentsCapture(0)).Arguments.Should().Equal(expected);
        Encoding.UTF8.GetString(File.ReadAllBytes(model.StdinCapture(0))).Should().Be("/mail-backfill alice@acme.example AQMkInbox0001 2026-09-30T10:00:00Z 25");
        Encoding.UTF8.GetString(File.ReadAllBytes(model.StdinCapture(1))).Should().Be($"/mail-backfill alice@acme.example AQMkInbox0001 {Earlier} 25");
    }
}
