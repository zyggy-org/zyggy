using System.Text;

using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Memory;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Core.Tests.LinkedIn;

namespace Zyggy.Core.Tests.Memory;

/// <summary>
/// Spec 38 AC-24: <c>archive list</c> shows an offloaded item as <c>offloaded → &lt;drive_path&gt;</c> (never <c>orphan</c>) and an item's
/// offload failures after its 37 state; every 37 row stays byte-identical; never git, never Graph.
/// </summary>
public sealed class ArchiveListOffloadTests : IDisposable
{
    private const string TextItem = "Quote for the Zyggy roof repair.\nTotal 1 234,00 EUR, valid 30 days.\n";

    private readonly MemoryTree _tree = new();
    private readonly RecordingProcessRunner _git = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero));
    private readonly Dictionary<string, string?> _environment;
    private readonly ArchiveStatePaths _state;

    public ArchiveListOffloadTests()
    {
        _environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["HOME"] = Path.Combine(_tree.Root, "home"),
            ["ZYGGY_STATE_DIR"] = Path.Combine(_tree.Root, "state"),
            ["ZYGGY_MEMORY_ROOT"] = _tree.Root,
            ["ZYGGY_TENANT"] = "acme",
            ["ZYGGY_USER"] = "alice",
            ["ZYGGY_TIMEZONE"] = "Europe/Brussels",
            ["ZYGGY_SECRET_PATTERNS"] = Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt"),
        };
        _state = new ArchiveStatePaths(_environment);
    }

    public void Dispose() => _tree.Dispose();

    [Fact]
    public async Task List_Offloaded_StateOffloadedNeverOrphan()
    {
        // Arrange
        _tree.Write("archive/zyggy/quote-2026.md", GoldenText("offload/sidecar-offloaded.md"));

        // Act
        var (exit, console) = await RunAsync();

        // Assert
        exit.Should().Be(ArchiveVerb.Archived, console.Stderr);
        console.Stdout.Should().Be("archive/zyggy/quote-2026.txt  text/plain  68 B  offloaded → Zyggy/archive/zyggy/quote-2026.txt  — Roof repair quote\n");
    }

    [Fact]
    public async Task List_Text_EqualsGolden()
    {
        // Arrange
        FourRowTree();

        // Act
        var (exit, console) = await RunAsync();

        // Assert
        exit.Should().Be(ArchiveVerb.Archived, console.Stderr);
        console.Stderr.Should().BeEmpty();
        console.Stdout.Should().Be(GoldenText("offload/list-offloaded.txt"));
    }

    [Fact]
    public async Task List_Json_EqualsGolden()
    {
        // Arrange
        FourRowTree();

        // Act
        var (exit, console) = await RunAsync("--json");

        // Assert
        exit.Should().Be(ArchiveVerb.Archived, console.Stderr);
        console.Stdout.Should().Be(GoldenText("offload/list-offloaded.json"));
    }

    [Fact]
    public async Task List_37RowsByteIdenticalWithStatePresent()
    {
        // Arrange: 37's three-row tree, and a state file with nothing about these items
        ThreeRowTree();
        var state = new OffloadState();
        state.RecordRun(_clock.GetUtcNow(), false, null);
        state.Save(_state);

        // Act
        var (_, text) = await RunAsync();
        var (_, json) = await RunAsync("--json");

        // Assert
        text.Stdout.Should().Be(GoldenText("list-text.txt"));
        json.Stdout.Should().Be(GoldenText("list.json"));
    }

    [Fact]
    public async Task List_Unindexed_ExcludesOffloaded()
    {
        // Arrange
        FourRowTree();

        // Act
        var (_, console) = await RunAsync("--unindexed");

        // Assert
        console.Stdout.Should().Be("archive/zyggy/roof-photo.png  image/png  65 B  unindexed  parked: target_conflict  — Photo of the roof damage\n");
    }

    [Fact]
    public async Task List_BrokenStateFile_RowsWithoutFailureDataNote()
    {
        // Arrange
        ThreeRowTree();
        Directory.CreateDirectory(_state.StateDirectory);
        File.WriteAllText(_state.StateFile, "{not json");

        // Act
        var (exit, console) = await RunAsync();

        // Assert
        exit.Should().Be(ArchiveVerb.Archived);
        console.Stdout.Should().Be(GoldenText("list-text.txt"));
        console.Stderr.Should().Be("archive: note: offload-state.json is not readable; offload failures are not shown\n");
    }

    [Fact]
    public async Task List_NeverCallsGitOrGraph()
    {
        // Arrange
        FourRowTree();

        // Act
        await RunAsync();

        // Assert
        _git.Calls.Should().BeEmpty();
        typeof(ArchiveService).GetConstructors().SelectMany(c => c.GetParameters())
            .Should().NotContain(p => p.ParameterType.Namespace!.StartsWith("Zyggy.Core.M365", StringComparison.Ordinal));
    }

    private static string GoldenText(string name) => Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(Golden.Directory, "archive", name)));

    private void WriteBytes(string relative, byte[] bytes)
    {
        var full = _tree.Full(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
    }

    // 37's tree: quote-2026 indexed, roof-photo unindexed, roof-invoice an orphan sidecar.
    private void ThreeRowTree()
    {
        WriteBytes("archive/zyggy/quote-2026.txt", new UTF8Encoding(false).GetBytes(TextItem));
        _tree.Write("archive/zyggy/quote-2026.md", GoldenText("sidecar-text.md"));
        WriteBytes("archive/zyggy/roof-photo.png", ImageBytes.Png(2, 2));
        _tree.Write("archive/zyggy/roof-photo.md", GoldenText("sidecar-png.md"));
        _tree.Write("archive/zyggy/roof-invoice.md", GoldenText("sidecar-pdf.md"));
        _tree.Write(
            "business/areas/zyggy.md",
            "---\nname: zyggy\ndescription: Zyggy\nupdated: 2026-10-01\n---\n- [stated] 2026-09-30: Archived \"Quote 2026\" (text/plain, 68 B) at archive/zyggy/quote-2026.txt — Roof repair quote\n");
    }

    // The golden README's four rows: fence-quote failing, quote-2026 offloaded, roof-invoice orphan, roof-photo parked.
    private void FourRowTree()
    {
        WriteBytes("archive/zyggy/fence-quote.txt", new UTF8Encoding(false).GetBytes(TextItem));
        _tree.Write("archive/zyggy/fence-quote.md", GoldenText("sidecar-text.md"));
        _tree.Write("archive/zyggy/quote-2026.md", GoldenText("offload/sidecar-offloaded.md"));
        WriteBytes("archive/zyggy/roof-photo.png", ImageBytes.Png(2, 2));
        _tree.Write("archive/zyggy/roof-photo.md", GoldenText("sidecar-png.md"));
        _tree.Write("archive/zyggy/roof-invoice.md", GoldenText("sidecar-pdf.md"));
        _tree.Write(
            "business/areas/zyggy.md",
            "---\nname: zyggy\ndescription: Zyggy\nupdated: 2026-10-01\n---\n- [stated] 2026-09-30: Archived \"Quote 2026\" (text/plain, 68 B) at archive/zyggy/fence-quote.txt — Roof repair quote\n"
            + "- [observed] 2026-10-01: Moved archive/zyggy/quote-2026.txt to OneDrive Zyggy/archive/zyggy/quote-2026.txt\n");
        var state = new OffloadState();
        var day = new DateOnly(2026, 10, 1);
        state.RecordTransient("zyggy/fence-quote", OffloadReason.TransportError, 3, day);
        state.RecordTransient("zyggy/fence-quote", OffloadReason.Throttled, 3, day);
        state.Park("zyggy/roof-photo", OffloadReason.TargetConflict, day);
        state.Save(_state);
    }

    private async Task<(int Exit, VerbConsole Console)> RunAsync(params string[] args)
    {
        var console = new VerbConsole();
        var exit = await new ArchiveVerb(_environment, _clock, FindTimeZone, _git).RunAsync(["list", .. args], console.Io, TestContext.Current.CancellationToken);
        return (exit, console);
    }

    private static TimeZoneInfo FindTimeZone(string id) => TimeZoneInfo.CreateCustomTimeZone(id, TimeSpan.FromHours(2), id, id);
}
