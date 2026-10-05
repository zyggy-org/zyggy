using NSubstitute;

using Zyggy.Core.M365;
using Zyggy.Core.M365.Runs;
using Zyggy.Core.Models;

namespace Zyggy.Core.Tests.M365;

/// <summary>
/// The files backfill — <c>files-backfill.sh</c> (spec 33 AC-32): the listing walked after the cursor, the skip classes before any model run,
/// the data-fenced prompt, the cursor moved only by a confirming counts line, the drive pre-check.
/// </summary>
public sealed class FilesBackfillTests : IDisposable
{
    private RunFixture _run = new();

    public void Dispose() => _run.Dispose();

    // The model confirms exactly the files it was given.
    private static string Confirm(ModelRunRequest request, int call)
    {
        var n = int.Parse(request.Prompt.Split('\n')[0].Split(' ')[^1], System.Globalization.CultureInfo.InvariantCulture);
        return $"Done.\nfiles-backfill batch: listed {n}, parsed {n}, skipped 0 (type 0, size 0, path 0, parse error 0, secret pattern 0), facts 2 (1 dup, 0 refused)";
    }

    private Task<RunOutcome> RunAsync(string? drive = "OneDrive", bool reset = false, CancellationToken? cancellationToken = null) =>
        new FilesBackfill(_run.Session, _run.Partition, _run.Graph.Reader, _run.Model, _run.Graph.Clock).RunAsync(drive, reset, cancellationToken ?? TestContext.Current.CancellationToken);

    private string? Cursor() => _run.StateFiles.Get(new StateEntry("files-backfill-watermark", "files-backfill-b!onedrive0001.watermark", StateGrammar.Cursor))?.TrimEnd('\n');

    [Fact]
    public async Task Run_TwoBatches_SkipsCountedCursorMovedDone()
    {
        // Arrange
        _run.Acts(Confirm);

        // Act
        var outcome = await RunAsync();

        // Assert
        outcome.Exit.Should().Be(0, string.Join('\n', outcome.StderrLines));
        outcome.StdoutLines.Should().Equal(
            "files-backfill: starting drive OneDrive from the beginning",
            "files-backfill: OneDrive batch 1: listed 10, parsed 10, skipped 0, facts 2, cost 0.10",
            "files-backfill: OneDrive batch 2: listed 9, parsed 6, skipped 3, facts 2, cost 0.10",
            "files-backfill: OneDrive done",
            "files-backfill: done — drives 1 (excluded 0, forbidden 0), listed 19, parsed 16, skipped 3 (type 2, size 1, path 0, parse error 0, secret pattern 0), facts 4 (2 duplicates dropped, 0 refused), batches 2, turns 8, cost 0.20 (cap 60.0)");
        Cursor().Should().Be("2026-09-03T08:00:00Z|01F20");
        Directory.EnumerateDirectories(_run.DownloadRoot).Should().BeEmpty();
    }

    [Fact]
    public async Task Run_PromptWithFenceExact()
    {
        // Arrange
        _run.Acts(Confirm);

        // Act
        await RunAsync();

        // Assert
        var request = _run.Requests[1];
        var runDirectory = request.Environment["ZYGGY_M365_RUN_DIR"];
        Path.GetFileName(runDirectory).Should().MatchRegex("^zyggy-m365-files\\.[A-Za-z0-9]{6}$");
        request.Prompt.Should().Be(
            $"/files-backfill b!onedrive0001 {runDirectory} 6\n<zyggy-m365-data>\n" +
            "01F11\tdocx\t2026-09-01\t/Reports/report-11.docx\n" +
            "01F12\tdocx\t2026-09-01\t/Reports/report-12.docx\n" +
            "01F14\tpdf\t2026-09-02\t/Reports/Q3/plan.pdf\n" +
            "01F16\tdocx\t2026-09-02\t/Archive/old.docx\n" +
            "01F17\tmd\t2026-09-02\t/notes.md\n" +
            "01F19\tcsv\t2026-09-03\t/data.csv\n" +
            "</zyggy-m365-data>");
        request.AllowedTools.Should().Equal(File.ReadAllLines(M365Run.Golden("run-lists", "files-backfill-allow.txt")));
        request.DisallowedTools.Should().Equal(File.ReadAllLines(M365Run.Golden("run-lists", "files-backfill-deny.txt")));
    }

    [Fact]
    public async Task Run_CursorStrictlyAfterIsoPipeId_TieGroupRest()
    {
        // Arrange: the shell left a cursor in the middle of a second shared by twelve files
        _run.StateFiles.Set(new StateEntry("files-backfill-watermark", "files-backfill-b!onedrive0001.watermark", StateGrammar.Cursor), "2026-09-01T08:00:00Z|01F05");
        _run.Acts(Confirm);

        // Act
        await RunAsync();

        // Assert
        _run.Requests[0].Prompt.Split('\n')[2].Should().StartWith("01F06\t");
    }

    [Fact]
    public async Task Run_PlainIsoCursor_ResumesAtItsSecond()
    {
        // Arrange: a plain ISO written before the item id was part of the cursor
        _run.StateFiles.Set(new StateEntry("files-backfill-watermark", "files-backfill-b!onedrive0001.watermark", StateGrammar.Cursor), "2026-09-02T08:00:00Z");
        _run.Acts(Confirm);

        // Act
        var outcome = await RunAsync();

        // Assert
        outcome.StdoutLines[0].Should().Be("files-backfill: starting drive OneDrive from 2026-09-02T08:00:00Z");
        _run.Requests[0].Prompt.Split('\n')[2].Should().StartWith("01F14\t");
    }

    [Fact]
    public async Task Run_DrivePrecheck403_SkippedCountedForbidden()
    {
        // Arrange
        _run.Graph.Stub.Always("GET", "drives/b!ops0001/root$", 403, File.ReadAllText(Infrastructure.StubGraphHandler.GraphFixture("graph-forbidden.json")));
        _run.Acts(Confirm);

        // Act
        var outcome = await RunAsync("ops");

        // Assert
        outcome.Exit.Should().Be(0);
        outcome.StderrLines.Should().Contain("files-backfill: drive ops: 403 (not granted), skipped — runbook 13 \"Grant another site\"");
        outcome.StdoutLines[^1].Should().StartWith("files-backfill: done — drives 1 (excluded 0, forbidden 1), ");
        await _run.Model.DidNotReceive().RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>());
        _run.Checkpoint("files-backfill.json")["drives"]!["b!ops0001"]!["forbidden"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task Run_SkipClasses_NeverReachModel()
    {
        // Arrange: everything under /Reports excluded by path; photo.JPG and chart.png by type; scan.pdf by size
        _run.Dispose();
        _run = new RunFixture("""{"drives":{"exclude_paths":["/Reports/"]}}""");
        _run.Acts(Confirm);

        // Act
        var outcome = await RunAsync();

        // Assert
        outcome.Exit.Should().Be(0, string.Join('\n', outcome.StderrLines));
        _run.Requests.SelectMany(r => r.Prompt.Split('\n').Skip(2)).Should().NotContain(l => l.Contains("/Reports/", StringComparison.Ordinal) || l.Contains(".JPG", StringComparison.Ordinal) || l.Contains("scan.pdf", StringComparison.Ordinal));
        outcome.StdoutLines[^1].Should().Contain("(type 2, size 1, path 13, ");
    }

    [Fact]
    public async Task Run_UnconfirmedBatch_StopsDriveExitFiveCursorUnmoved()
    {
        // Arrange
        _run.Acts((_, _) => "I did my best.");

        // Act
        var outcome = await RunAsync();

        // Assert
        outcome.Exit.Should().Be(5);
        outcome.StderrLines.Should().Contain("files-backfill: OneDrive: batch of 10 files not confirmed by a counts line, stopping the drive");
        outcome.StderrLines[^1].Should().Be("files-backfill: stopped: batch not confirmed in OneDrive");
        Cursor().Should().BeNull();
    }

    [Fact]
    public async Task Run_IsError_ExitSixCursorUnmoved()
    {
        // Arrange
        _run.Model.RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>()).Returns(RunFixture.Error());

        // Act
        var outcome = await RunAsync();

        // Assert
        outcome.Exit.Should().Be(6);
        Cursor().Should().BeNull();
        Directory.EnumerateDirectories(_run.DownloadRoot).Should().BeEmpty();
    }

    [Fact]
    public async Task Run_Cancelled_RunDirRemovedCursorUnmoved_Interrupted()
    {
        // Arrange
        using var cancel = new CancellationTokenSource();
        _run.Acts((_, _) =>
        {
            cancel.Cancel();
            throw new OperationCanceledException(cancel.Token);
        });

        // Act
        var outcome = await RunAsync(cancellationToken: cancel.Token);

        // Assert
        outcome.Exit.Should().Be(130);
        Cursor().Should().BeNull();
        Directory.EnumerateDirectories(_run.DownloadRoot).Should().BeEmpty();
    }

    [Theory]
    [InlineData("""{"files_backfill":{"budget_usd_total":0.1}}""", "budget 0.10 USD over cap 0.1")]
    [InlineData("""{"files_backfill":{"max_facts":2}}""", "facts 2 at cap 2")]
    public async Task Run_Cap_ExitFive(string patch, string reason)
    {
        // Arrange
        _run.Dispose();
        _run = new RunFixture(patch);
        _run.Acts(Confirm);

        // Act
        var outcome = await RunAsync();

        // Assert
        outcome.Exit.Should().Be(5);
        outcome.StderrLines[^1].Should().Be("files-backfill: stopped: " + reason);
    }

    [Theory]
    [InlineData("nowhere", "files-backfill: no drive nowhere among the granted drives")]
    public async Task Run_DriveUnknown_ExitFour(string option, string message)
    {
        // Act
        var outcome = await RunAsync(option);

        // Assert
        outcome.Exit.Should().Be(4);
        outcome.StderrLines[^1].Should().Be(message);
    }

    [Fact]
    public async Task Run_HooksOff_ExitFive()
    {
        // Act
        var (exit, console) = await M365Run.RunAsync(new Dictionary<string, string?> { ["ZYGGY_HOOKS"] = "off" }, _run.Graph.Clock, ["files-backfill"]);

        // Assert
        exit.Should().Be(5);
        console.Stderr.Should().Be("files-backfill: refused: unattended run (ZYGGY_HOOKS=off)\n");
    }
}
