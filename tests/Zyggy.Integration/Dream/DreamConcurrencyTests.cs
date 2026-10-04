using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

using Zyggy.Core.Dream;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Dream;

/// <summary>AC-11, AC-8, AC-26 (integration): writers during a run, a killed run, and a dead run's pending marker.</summary>
public sealed class DreamConcurrencyTests : IAsyncLifetime
{
    private const string Appended = "- [stated] 2026-10-01: Carol called on Sunday.";

    private readonly MemoryRepoFixture _repo = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Today => DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public async ValueTask InitializeAsync()
    {
        await _repo.InitializeAsync();
        await _repo.SeedAsync("dream", Ct);
    }

    public ValueTask DisposeAsync() => _repo.DisposeAsync();

    private static async Task WaitForFileAsync(string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!File.Exists(path))
        {
            DateTime.UtcNow.Should().BeBefore(deadline, $"{path} should appear while the model runs");
            await Task.Delay(50, Ct);
        }
    }

    [Fact]
    public async Task Dream_LineAppendedWhileModelRuns_OfferedNextRunNotLost()
    {
        // Arrange
        var stdin1 = Path.Combine(_repo.RootDir, "stdin-1.txt");
        var today = Path.Combine(_repo.PrincipalDir, "inbox", $"remember-{Today}.md");

        // Act: run 1 sleeps 3 s inside the model call; the test appends to today's inbox file meanwhile.
        var first = _repo.DreamAsync("dream-file-ok", Ct, new Dictionary<string, string?>
        {
            ["ZYGGY_FAKE_CLAUDE_DELAY_MS"] = "3000",
            ["ZYGGY_FAKE_CLAUDE_STDIN_CAPTURE"] = stdin1,
        });
        await WaitForFileAsync(stdin1);
        await File.AppendAllTextAsync(today, Appended + "\n", Ct);
        var run1 = await first;

        var stdin2 = Path.Combine(_repo.RootDir, "stdin-2.txt");
        var run2 = await _repo.DreamAsync("dream-file-ok-2", Ct, new Dictionary<string, string?> { ["ZYGGY_FAKE_CLAUDE_STDIN_CAPTURE"] = stdin2 });

        // Assert
        run1.ExitCode.Should().Be(0, run1.Stderr);
        (await File.ReadAllTextAsync(stdin1, Ct)).Should().NotContain(Appended);
        run2.ExitCode.Should().Be(0, run2.Stderr);
        var offered = await File.ReadAllTextAsync(stdin2, Ct);
        offered.Split('\n').Count(l => l.EndsWith(Appended, StringComparison.Ordinal)).Should().Be(1);
        offered.Should().NotContain("Carol likes green tea.", "lines consumed by run 1 are never offered again");
        (await _repo.ShowAsync("acme/alice/private/people/carol.md", Ct)).Should().Contain(Appended);
    }

    [Fact]
    public async Task Dream_InventoryFileReplacedBetweenRuns_NewLinesOfferedVanishedIgnored()
    {
        // Arrange
        (await _repo.DreamAsync("dream-file-ok", Ct)).ExitCode.Should().Be(0);
        var inventory = Path.Combine(_repo.PrincipalDir, "inbox", "github-inventory-2026-09-29.md");
        const string Kept = "- [observed] 2026-09-29 [github-inventory 2026-09-29]: Repository zyggy is private on GitHub.";
        const string Fresh = "- [observed] 2026-10-02 [github-inventory 2026-10-02]: Repository calizr was created.";
        await File.WriteAllTextAsync(inventory, Kept + "\n" + Fresh + "\n", Ct);
        var stdin = Path.Combine(_repo.RootDir, "stdin.txt");

        // Act: the model fails on purpose; only the offered lines matter.
        await _repo.DreamAsync("error", Ct, new Dictionary<string, string?> { ["ZYGGY_FAKE_CLAUDE_STDIN_CAPTURE"] = stdin, ["ZYGGY_FAKE_CLAUDE_EXIT"] = "1" });

        // Assert
        var offered = (await File.ReadAllTextAsync(stdin, Ct)).Split('\n').Where(l => l.StartsWith('L')).ToList();
        offered.Should().ContainSingle().Which.Should().EndWith(Fresh);
    }

    [Fact]
    public async Task Dream_ProcessKilledDuringModelCall_NextRunNotLockedAndCompletes()
    {
        // Arrange
        var stdin = Path.Combine(_repo.RootDir, "stdin-killed.txt");
        using var running = ZyggyCli.Start(["dream"], _repo.DreamEnv("dream-file-ok", new Dictionary<string, string?>
        {
            ["ZYGGY_FAKE_CLAUDE_DELAY_MS"] = "30000",
            ["ZYGGY_FAKE_CLAUDE_STDIN_CAPTURE"] = stdin,
        }), _repo.RootDir);
        await WaitForFileAsync(stdin);

        // Act
        running.Kill(entireProcessTree: true);
        await running.WaitForExitAsync(Ct);
        var next = await _repo.DreamAsync("dream-file-ok", Ct);

        // Assert
        next.ExitCode.Should().Be(0, next.Stderr + next.Stdout);
        DreamRunRecordStore.ReadLast(_repo.StateDir)!.Outcome.Should().Be("committed");
        (await _repo.ShowAsync("acme/alice/private/people/carol.md", Ct)).Should().Contain("Carol likes green tea.");
    }

    [Fact]
    public async Task Dream_PendingMarkerFromDeadRun_RestoredThenRunCompletes()
    {
        // Arrange: a dead run wrote carol.md and a new file, then died before its commit.
        var carol = Path.Combine(_repo.PrincipalDir, "private", "people", "carol.md");
        await File.AppendAllTextAsync(carol, "- [stated] 2026-09-29: written by the dead run.\n", Ct);
        var created = Path.Combine(_repo.PrincipalDir, "business", "areas", "dead.md");
        await File.WriteAllTextAsync(created, "- [observed] 2026-09-29 [x 2026-09-29]: dead run.\n", Ct);
        var marker = new JsonObject
        {
            ["run"] = "01JDEAD",
            ["files"] = new JsonArray(
                new JsonObject { ["path"] = "private/people/carol.md", ["sha256_written"] = MemorySnapshot.Hash(File.ReadAllBytes(carol)), ["existed_before"] = true, ["sha256_before"] = new string('a', 64) },
                new JsonObject { ["path"] = "business/areas/dead.md", ["sha256_written"] = MemorySnapshot.Hash(File.ReadAllBytes(created)), ["existed_before"] = false, ["sha256_before"] = null }),
        };
        Directory.CreateDirectory(Path.Combine(_repo.PrincipalDir, ".dream"));
        await File.WriteAllTextAsync(Path.Combine(_repo.PrincipalDir, ".dream", "pending.json"), marker.ToJsonString(), new UTF8Encoding(false), Ct);

        // Act
        var run = await _repo.DreamAsync("dream-file-ok", Ct);

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr + run.Stdout);
        var committed = await _repo.ShowAsync("acme/alice/private/people/carol.md", Ct);
        committed.Should().Contain("Carol likes green tea.").And.NotContain("dead run");
        File.Exists(created).Should().BeFalse();
        File.Exists(Path.Combine(_repo.PrincipalDir, ".dream", "pending.json")).Should().BeFalse();
    }
}
