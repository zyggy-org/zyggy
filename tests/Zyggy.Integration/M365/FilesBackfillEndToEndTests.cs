using System.Text;

using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.M365;

/// <summary>
/// Spec 33 AC-32 (I, in process): <c>zyggy m365 files-backfill</c> through the real process runner and <c>tools/fake-claude</c> — a
/// confirmed batch moves the cursor and removes its run directory; a stop removes it and leaves the cursor where it was.
/// </summary>
public sealed class FilesBackfillEndToEndTests : IDisposable
{
    // One batch covers the sixteen readable files of the fixture drive, so the scenario's counts line confirms it.
    private readonly M365RunHarness _run = new("""{"files_backfill":{"batch_files":16}}""");

    public void Dispose() => _run.Dispose();

    private string CursorFile => Path.Combine(_run.State, "files-backfill-b!onedrive0001.watermark");

    [Fact]
    public async Task FilesBackfill_Batch_CursorMovedRunDirRemoved()
    {
        // Arrange
        var model = _run.Model(_ => "m365-files-batch");

        // Act
        var (exit, console) = await _run.RunAsync(model, ["files-backfill", "--drive", "OneDrive"], TestContext.Current.CancellationToken);

        // Assert
        exit.Should().Be(0, console.Stderr);
        console.Stdout.Split('\n')[1].Should().Be("files-backfill: OneDrive batch 1: listed 18, parsed 14, skipped 4, facts 4, cost 0.38");
        File.ReadAllText(CursorFile).Should().Be("2026-09-03T08:00:00Z|01F20\n");
        var runDirectory = model.Requests.Single().Environment["ZYGGY_M365_RUN_DIR"];
        Encoding.UTF8.GetString(File.ReadAllBytes(model.StdinCapture(0))).Should().StartWith(
            $"/files-backfill b!onedrive0001 {runDirectory} 16\n<zyggy-m365-data>\n01F01\tdocx\t2026-09-01\t/Reports/report-01.docx\n");
        _run.RunDirectories.Should().BeEmpty();
    }

    [Fact]
    public async Task FilesBackfill_CapturedArgs_RunMcpConfigOnlyM365_DenyIncludesLinkedIn()
    {
        // Arrange: spec 36 AC-8
        string? loaded = null;
        var model = _run.Model(_ => "m365-files-batch", act: (request, _, _) => loaded ??= File.ReadAllText(request.McpConfig!));

        // Act
        var (exit, console) = await _run.RunAsync(model, ["files-backfill", "--drive", "OneDrive"], TestContext.Current.CancellationToken);

        // Assert
        exit.Should().Be(0, console.Stderr);
        var arguments = FakeClaude.ReadCapture(model.ArgumentsCapture(0)).Arguments;
        arguments.Should().ContainInConsecutiveOrder("--strict-mcp-config", "--mcp-config", Path.Join(_run.Fixture.StateDirectory, "m365", "run-mcp.json"));
        arguments[arguments.ToList().IndexOf("--disallowedTools") + 1].Split(',').Should().Contain(["mcp__linkedin__*", "Bash(zyggy linkedin *)"]);
        loaded.Should().Be(File.ReadAllText(M365InstanceFixture.Golden("m365", "run-mcp.json")));
    }

    [Fact]
    public async Task FilesBackfill_Cancelled_RunDirRemovedCursorUnmoved()
    {
        // Arrange
        var model = _run.Model(_ => "m365-files-batch", _ => 30000);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var stopper = M365RunHarness.CancelWhenStartedAsync(model, 0, cancel);

        // Act
        var (exit, _) = await _run.RunAsync(model, ["files-backfill", "--drive", "OneDrive"], cancel.Token);
        await stopper;

        // Assert
        exit.Should().Be(130);
        File.Exists(CursorFile).Should().BeFalse();
        _run.RunDirectories.Should().BeEmpty();
        ProcessProbe.ProcessesWithWorkingDirectory(_run.Fixture.Checkout).Should().Be(0);
    }
}
