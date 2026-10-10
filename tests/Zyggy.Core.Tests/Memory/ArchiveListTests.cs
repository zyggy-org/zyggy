using System.Text;

using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Memory;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Core.Tests.LinkedIn;

namespace Zyggy.Core.Tests.Memory;

/// <summary>
/// <c>zyggy memory archive list</c> (spec 37 AC-18): one row per item in ordinal path order with type, size, <c>indexed</c>/<c>unindexed</c>/<c>orphan</c>
/// and description, as text or JSON (goldens over a fixed three-row tree); the project and unindexed filters; never git; runs under <c>ZYGGY_HOOKS=off</c>.
/// </summary>
public sealed class ArchiveListTests : IDisposable
{
    private const string TextItem = "Quote for the Zyggy roof repair.\nTotal 1 234,00 EUR, valid 30 days.\n";

    private readonly MemoryTree _tree = new();
    private readonly RecordingProcessRunner _git = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero));
    private readonly Dictionary<string, string?> _environment;

    public ArchiveListTests()
    {
        _environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["HOME"] = Path.Combine(_tree.Root, "home"),
            ["ZYGGY_MEMORY_ROOT"] = _tree.Root,
            ["ZYGGY_TENANT"] = "acme",
            ["ZYGGY_USER"] = "alice",
            ["ZYGGY_TIMEZONE"] = "Europe/Brussels",
            ["ZYGGY_SECRET_PATTERNS"] = Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt"),
        };
    }

    public void Dispose() => _tree.Dispose();

    private static TimeZoneInfo FindTimeZone(string id) => TimeZoneInfo.CreateCustomTimeZone(id, TimeSpan.FromHours(2), id, id);

    private static string GoldenText(string name) =>
        Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(Golden.Directory, "archive", name)));

    private void WriteBytes(string relative, byte[] bytes)
    {
        var full = _tree.Full(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
    }

    // One indexed text item, one unindexed PNG, one orphan sidecar (the PDF's item is missing).
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

    private async Task<(int Exit, VerbConsole Console)> RunAsync(params string[] args)
    {
        var console = new VerbConsole();
        var exit = await new ArchiveVerb(_environment, _clock, FindTimeZone, _git).RunAsync(["list", .. args], console.Io, TestContext.Current.CancellationToken);
        return (exit, console);
    }

    private async Task<string[]> RowsAsync(params string[] args)
    {
        var (exit, console) = await RunAsync(args);
        exit.Should().Be(ArchiveVerb.Archived, console.Stderr);
        return console.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public async Task List_ThreeRowTree_TextEqualsGolden()
    {
        // Arrange
        ThreeRowTree();

        // Act
        var (exit, console) = await RunAsync();

        // Assert
        exit.Should().Be(ArchiveVerb.Archived, console.Stderr);
        console.Stderr.Should().BeEmpty();
        console.Stdout.Should().Be(GoldenText("list-text.txt"));
    }

    [Fact]
    public async Task List_ThreeRowTree_JsonEqualsGolden()
    {
        // Arrange
        ThreeRowTree();

        // Act
        var (exit, console) = await RunAsync("--json");

        // Assert
        exit.Should().Be(ArchiveVerb.Archived, console.Stderr);
        console.Stdout.Should().Be(GoldenText("list.json"));
    }

    [Fact]
    public async Task List_Indexed_WhenAnySideFileContainsItemPath()
    {
        // Arrange: the path is named in a private file, not the project's own.
        WriteBytes("archive/house-move/plan.pdf", "%PDF-1.4\n%%EOF\n"u8.ToArray());
        _tree.Write("archive/house-move/plan.md", GoldenText("sidecar-pdf.md").Replace("project: zyggy", "project: house-move", StringComparison.Ordinal));
        _tree.Write("private/topics/moving.md", "---\nname: moving\ndescription: moving\nupdated: 2026-10-01\n---\n- [stated] 2026-09-30: plan at archive/house-move/plan.pdf\n");

        // Act
        var rows = await RowsAsync();

        // Assert
        rows.Should().ContainSingle().Which.Should().Contain("  indexed  ");
    }

    [Fact]
    public async Task List_Unindexed_WhenNoSideFileNamesIt()
    {
        // Arrange
        ThreeRowTree();

        // Act
        var rows = await RowsAsync();

        // Assert
        rows.Should().ContainSingle(r => r.StartsWith("archive/zyggy/roof-photo.png  ", StringComparison.Ordinal)).Which.Should().Contain("  unindexed  ");
    }

    [Fact]
    public async Task List_OrphanSidecar_StateOrphan()
    {
        // Arrange
        ThreeRowTree();

        // Act
        var rows = await RowsAsync();

        // Assert
        rows.Should().Contain("archive/zyggy/roof-invoice.md  -  -  orphan  — Invoice for the roof repair");
    }

    [Fact]
    public async Task List_OrphanItem_StateOrphan()
    {
        // Arrange
        WriteBytes("archive/zyggy/loose.pdf", "%PDF-1.4\n%%EOF\n"u8.ToArray());

        // Act
        var rows = await RowsAsync();

        // Assert
        rows.Should().Equal("archive/zyggy/loose.pdf  application/pdf  15 B  orphan  — -");
    }

    [Fact]
    public async Task List_ProjectFilter_OnlyThatProject()
    {
        // Arrange
        ThreeRowTree();
        WriteBytes("archive/house-move/plan.pdf", "%PDF-1.4\n%%EOF\n"u8.ToArray());
        _tree.Write("archive/house-move/plan.md", GoldenText("sidecar-pdf.md").Replace("project: zyggy", "project: house-move", StringComparison.Ordinal));

        // Act
        var rows = await RowsAsync("--project", "house-move");

        // Assert
        rows.Should().ContainSingle().Which.Should().StartWith("archive/house-move/plan.pdf  ");
    }

    [Fact]
    public async Task List_UnindexedFlag_DropsIndexedAndOrphan()
    {
        // Arrange
        ThreeRowTree();

        // Act
        var rows = await RowsAsync("--unindexed");

        // Assert
        rows.Should().Equal("archive/zyggy/roof-photo.png  image/png  65 B  unindexed  — Photo of the roof damage");
    }

    [Fact]
    public async Task List_OrdinalPathOrder()
    {
        // Arrange
        WriteBytes("archive/zyggy/b.txt", "b\n"u8.ToArray());
        WriteBytes("archive/zyggy/a-2.txt", "a\n"u8.ToArray());
        WriteBytes("archive/alpha/z.txt", "z\n"u8.ToArray());
        WriteBytes("archive/zyggy/a.txt", "a\n"u8.ToArray());

        // Act
        var rows = await RowsAsync();

        // Assert
        rows.Select(r => r[..r.IndexOf("  ", StringComparison.Ordinal)]).Should().Equal(
            "archive/alpha/z.txt", "archive/zyggy/a-2.txt", "archive/zyggy/a.txt", "archive/zyggy/b.txt");
    }

    [Fact]
    public async Task List_NoArchiveDirectory_EmptyExitZero()
    {
        // Act
        var (exit, console) = await RunAsync();
        var (jsonExit, json) = await RunAsync("--json");

        // Assert
        exit.Should().Be(ArchiveVerb.Archived);
        console.Stdout.Should().BeEmpty();
        console.Stderr.Should().BeEmpty();
        jsonExit.Should().Be(ArchiveVerb.Archived);
        json.Stdout.Should().Be("[]\n");
    }

    [Fact]
    public async Task List_NeverCallsGit()
    {
        // Arrange
        ThreeRowTree();

        // Act
        await RowsAsync();

        // Assert
        _git.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task List_HooksOff_StillRuns()
    {
        // Arrange
        ThreeRowTree();
        _environment["ZYGGY_HOOKS"] = "off";

        // Act
        var (exit, console) = await RunAsync();

        // Assert
        exit.Should().Be(ArchiveVerb.Archived, console.Stderr);
        console.Stdout.Should().Be(GoldenText("list-text.txt"));
    }
}
