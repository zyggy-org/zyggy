using NSubstitute;

using Zyggy.Core.M365;
using Zyggy.Core.M365.Runs;
using Zyggy.Core.Models;

namespace Zyggy.Core.Tests.M365;

/// <summary>
/// The whole-mailbox backfill — <c>mail-backfill.sh</c> (spec 33 AC-31): folders minus exclusions, newest first in batches until one lists
/// 0 messages, the caps before every batch, the checkpoint after every completed batch; a stop writes nothing.
/// </summary>
public sealed class MailBackfillTests : IDisposable
{
    private const string Now = "2026-09-30T10:00:00Z";

    private RunFixture _run = new();

    public void Dispose() => _run.Dispose();

    // Each folder: one batch of 25 messages that moves the watermark one month back, then one that lists 0.
    private static string TwoBatchesPerFolder(ModelRunRequest request, int call, Zyggy.Core.M365.M365State state)
    {
        var folder = request.Prompt.Split(' ')[2];
        var watermark = request.Prompt.Split(' ')[3];
        if (watermark == Now)
        {
            state.Set(new StateEntry("backfill-watermark", $"backfill-{folder}.watermark", StateGrammar.Iso), "2026-08-30T10:00:00Z");
            return "Working.\nmail-backfill batch: messages 25, facts 10 (2 dup, 1 refused)";
        }

        return "mail-backfill batch: messages 0, facts 0 (0 dup, 0 refused)";
    }

    private Task<RunOutcome> RunAsync(string? folder = null, bool reset = false, CancellationToken? cancellationToken = null) =>
        new MailBackfill(_run.Session, _run.Partition, _run.Graph.Reader, _run.Model, _run.Graph.Clock).RunAsync(folder, reset, cancellationToken ?? TestContext.Current.CancellationToken);

    [Fact]
    public async Task Run_FoldersMinusExclusions_NewestFirstUntilZero()
    {
        // Arrange
        _run.Acts((r, c) => TwoBatchesPerFolder(r, c, _run.StateFiles));

        // Act
        var outcome = await RunAsync();

        // Assert
        outcome.Exit.Should().Be(0, string.Join('\n', outcome.StderrLines));
        outcome.StdoutLines.Should().Equal(
            $"mail-backfill: starting folder Inbox from {Now}",
            "mail-backfill: Inbox batch 1: messages 25, facts 10, cost 0.10",
            "mail-backfill: Inbox batch 2: messages 0, facts 0, cost 0.10",
            "mail-backfill: Inbox done",
            $"mail-backfill: starting folder Sent Items from {Now}",
            "mail-backfill: Sent Items batch 1: messages 25, facts 10, cost 0.10",
            "mail-backfill: Sent Items batch 2: messages 0, facts 0, cost 0.10",
            "mail-backfill: Sent Items done",
            $"mail-backfill: starting folder Archive from {Now}",
            "mail-backfill: Archive batch 1: messages 25, facts 10, cost 0.10",
            "mail-backfill: Archive batch 2: messages 0, facts 0, cost 0.10",
            "mail-backfill: Archive done",
            "mail-backfill: done — folders 3 (excluded 3), messages 75, batches 6, facts 30 (6 duplicates dropped, 3 refused), turns 24, cost 0.60 (cap 40.0)");
        var checkpoint = _run.Checkpoint("mail-backfill.json");
        checkpoint["folders"]!["AQMkInbox0001"]!["done"]!.GetValue<bool>().Should().BeTrue();
        checkpoint["folders"]!["AQMkInbox0001"]!["watermark"]!.GetValue<string>().Should().Be("2026-08-30T10:00:00Z");
        checkpoint.Select(kv => kv.Key).Should().Equal(
            "folders", "total_messages", "total_facts", "total_duplicates", "total_refused", "total_batches", "total_turns", "total_cost", "started", "updated");
    }

    [Fact]
    public async Task Run_Request_PromptListsCapsExact()
    {
        // Arrange
        _run.Acts((r, c) => TwoBatchesPerFolder(r, c, _run.StateFiles));

        // Act
        await RunAsync();

        // Assert
        var request = _run.Requests[0];
        request.Prompt.Should().Be($"/mail-backfill alice@acme.example AQMkInbox0001 {Now} 25");
        request.AllowedTools.Should().Equal(File.ReadAllLines(M365Run.Golden("run-lists", "mail-backfill-allow.txt")));
        request.DisallowedTools.Should().Equal(File.ReadAllLines(M365Run.Golden("run-lists", "mail-backfill-deny.txt")));
        request.MaxTurns.Should().Be(15);
        request.MaxBudgetUsd.Should().Be(0.5m);
        request.Model.Should().Be("sonnet");
        request.Environment.Should().NotContainKey("ZYGGY_M365_RUN_DIR");
    }

    [Fact]
    public async Task Run_Cancelled_DuringBatch2_CheckpointOfBatch1Stands_Interrupted130()
    {
        // Arrange
        using var cancel = new CancellationTokenSource();
        _run.Acts((r, c) =>
        {
            if (c == 1)
            {
                cancel.Cancel();
                throw new OperationCanceledException(cancel.Token);
            }

            return TwoBatchesPerFolder(r, c, _run.StateFiles);
        });

        // Act
        var outcome = await RunAsync(cancellationToken: cancel.Token);

        // Assert
        outcome.Exit.Should().Be(130);
        var inbox = _run.Checkpoint("mail-backfill.json")["folders"]!["AQMkInbox0001"]!;
        inbox["batches"]!.GetValue<double>().Should().Be(1);
        inbox["done"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task Run_StoppedBySigterm_ExitCodeReadWhenTheRunStops()
    {
        // Arrange: the signal arrives after the run started
        using var cancel = new CancellationTokenSource();
        int? signal = null;
        _run.Acts((_, _) =>
        {
            signal = 143;
            cancel.Cancel();
            throw new OperationCanceledException(cancel.Token);
        });

        // Act
        var outcome = await new MailBackfill(_run.Session, _run.Partition, _run.Graph.Reader, _run.Model, _run.Graph.Clock)
            .RunAsync(null, false, cancel.Token, () => signal);

        // Assert
        outcome.Exit.Should().Be(143);
    }

    [Theory]
    [InlineData("""{"mail_backfill":{"budget_usd_total":0.15}}""", "budget 0.20 USD over cap 0.15")]
    [InlineData("""{"mail_backfill":{"max_facts":10}}""", "facts 10 at cap 10")]
    [InlineData("""{"mail_backfill":{"max_messages":25}}""", "messages 25 at cap 25")]
    public async Task Run_Cap_ExitFiveCheckpointIntactCountsLine(string patch, string reason)
    {
        // Arrange
        _run.Dispose();
        _run = new RunFixture(patch);
        _run.Acts((r, c) => TwoBatchesPerFolder(r, c, _run.StateFiles));

        // Act
        var outcome = await RunAsync();

        // Assert
        outcome.Exit.Should().Be(5);
        outcome.StderrLines[^1].Should().Be("mail-backfill: stopped: " + reason);
        outcome.StdoutLines[^1].Should().StartWith("mail-backfill: stopped — folders 3 (excluded 3), ");
        File.Exists(Path.Combine(_run.State, "mail-backfill.json")).Should().BeTrue();
    }

    [Theory]
    [InlineData("Archive", "AQMkArchive0001")]
    [InlineData("sentitems", "AQMkSentItems0001")]
    [InlineData("AQMkInbox0001", "AQMkInbox0001")]
    public async Task Run_FolderOption_OnlyThatFolder(string option, string id)
    {
        // Arrange
        _run.Acts((r, c) => TwoBatchesPerFolder(r, c, _run.StateFiles));

        // Act
        var outcome = await RunAsync(option);

        // Assert
        outcome.Exit.Should().Be(0);
        _run.Requests.Should().OnlyContain(r => r.Prompt.Split(' ')[2] == id);
        outcome.StdoutLines[^1].Should().StartWith("mail-backfill: done — folders 1 (excluded 3), ");
    }

    [Theory]
    [InlineData("junkemail", "mail-backfill: folder junkemail is excluded (mail_backfill.exclude_folders)")]
    [InlineData("nowhere", "mail-backfill: no folder nowhere in the mailbox")]
    public async Task Run_FolderExcludedOrUnknown_ExitFour(string option, string message)
    {
        // Act
        var outcome = await RunAsync(option);

        // Assert
        outcome.Exit.Should().Be(4);
        outcome.StderrLines[^1].Should().Be(message);
        await _run.Model.DidNotReceive().RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Run_IsError_ExitSixCostCountedFolderNotAdvanced()
    {
        // Arrange
        _run.Model.RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>()).Returns(RunFixture.Error());

        // Act
        var outcome = await RunAsync();

        // Assert
        outcome.Exit.Should().Be(6);
        outcome.StderrLines[^1].Should().Be("mail-backfill: claude run failed (error_during_execution) — runbook 13 \"Model run failed\"");
        var checkpoint = _run.Checkpoint("mail-backfill.json");
        checkpoint["total_cost"]!.GetValue<double>().Should().Be(0.05);
        checkpoint["total_batches"]!.GetValue<double>().Should().Be(0);
        checkpoint["folders"]!["AQMkInbox0001"]!["turns"]!.GetValue<double>().Should().Be(3);
    }

    [Fact]
    public async Task Run_WatermarkNotAdvanced_StopsFolderExitFiveAtEnd()
    {
        // Arrange: the model never sets a watermark
        _run.Acts((_, _) => "mail-backfill batch: messages 25, facts 1 (0 dup, 0 refused)");

        // Act
        var outcome = await RunAsync();

        // Assert
        outcome.Exit.Should().Be(5);
        outcome.StderrLines.Should().Contain("mail-backfill: Inbox: watermark not advanced, stopping the folder");
        outcome.StderrLines[^1].Should().Be("mail-backfill: stopped: watermark not advanced in Inbox, Sent Items, Archive");
    }

    [Fact]
    public async Task Run_Reset_ClearsCheckpointAndWatermarks()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_run.State, "mail-backfill.json"), """{"folders":{"AQMkGone0001":{"done":true}},"total_cost":1}""" + "\n");
        File.WriteAllText(Path.Combine(_run.State, "backfill-AQMkInbox0001.watermark"), "2026-01-01T00:00:00Z\n");
        File.WriteAllText(Path.Combine(_run.State, "backfill-AQMkGone0001.watermark"), "2026-01-01T00:00:00Z\n");
        _run.Acts((r, c) => TwoBatchesPerFolder(r, c, _run.StateFiles));

        // Act
        var outcome = await RunAsync(reset: true);

        // Assert
        outcome.StdoutLines[0].Should().Be("mail-backfill: checkpoint and backfill watermarks reset");
        File.Exists(Path.Combine(_run.State, "backfill-AQMkGone0001.watermark")).Should().BeFalse();
        _run.Requests[0].Prompt.Should().Contain($" {Now} ");
    }

    [Fact]
    public async Task Run_HooksOff_ExitFiveBeforeArguments()
    {
        // Act
        var (exit, console) = await M365Run.RunAsync(new Dictionary<string, string?> { ["ZYGGY_HOOKS"] = "off" }, _run.Graph.Clock, ["mail-backfill", "--bogus"]);

        // Assert
        exit.Should().Be(5);
        console.Stderr.Should().Be("mail-backfill: refused: unattended run (ZYGGY_HOOKS=off)\n");
    }

    [Theory]
    [InlineData(new[] { "--bogus" }, "unknown argument '--bogus'")]
    [InlineData(new[] { "--folder" }, "--folder needs <name>")]
    [InlineData(new[] { "--folder", "--reset" }, "--folder needs <name>")]
    [InlineData(new[] { "--reset", "--reset" }, "--reset given twice")]
    [InlineData(new[] { "--folder", "a", "--folder", "b" }, "--folder given twice")]
    public async Task Run_BadArguments_ExitFour(string[] args, string message)
    {
        // Act
        var (exit, console) = await M365Run.RunAsync(new Dictionary<string, string?>(), _run.Graph.Clock, ["mail-backfill", .. args]);

        // Assert
        exit.Should().Be(4);
        console.Stderr.Should().Be($"mail-backfill: {message} (usage: zyggy m365 mail-backfill [--folder <name>] [--reset])\n");
    }
}
