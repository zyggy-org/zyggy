using System.Globalization;
using System.Text;

using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Memory;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Memory;

/// <summary>Spec 38 AC-27: the caps count only bytes on disk, so an offloaded item frees space; its slug stays taken (the sidecar remains).</summary>
public sealed class ArchiveAddAfterOffloadTests : IDisposable
{
    private readonly MemoryTree _tree = new();
    private readonly RecordingProcessRunner _git = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 2, 10, 0, 0, TimeSpan.Zero));
    private readonly string _home;
    private readonly string _source;
    private readonly Dictionary<string, string?> _environment;

    public ArchiveAddAfterOffloadTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "zyggy-ut-add-offload", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_home, "Downloads"));
        _source = Path.Combine(_home, "Downloads", "fence.txt");
        File.WriteAllBytes(_source, new UTF8Encoding(false).GetBytes("Quote for the garden fence.\nTotal 800,00 EUR.\n"));
        _environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["HOME"] = _home,
            ["ZYGGY_MEMORY_ROOT"] = _tree.Root,
            ["ZYGGY_TENANT"] = "acme",
            ["ZYGGY_USER"] = "alice",
            ["ZYGGY_TIMEZONE"] = "Europe/Brussels",
            ["ZYGGY_SECRET_PATTERNS"] = Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt"),
            ["ZYGGY_ARCHIVE_CONFIG"] = Path.Combine(_home, "archive.json"),
        };
        _tree.Write("business/areas/zyggy.md", "---\nname: Zyggy\ndescription: the agent platform\nupdated: 2026-09-01\n---\n");

        // A large item already offloaded-in-waiting: its bytes are still in the archive, its sidecar already carries the offload part.
        _tree.Write("archive/zyggy/plan.txt", string.Concat(Enumerable.Repeat("Roof plan line.\n", 500)));
        _tree.Write("archive/zyggy/plan.md", Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(Golden.Directory, "archive", "offload", "sidecar-offloaded.md"))));
    }

    public void Dispose()
    {
        _tree.Dispose();
        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, recursive: true);
        }
    }

    [Fact]
    public async Task Add_ProjectWithOffloadedItem_CapsCountOnlyBytesOnDisk()
    {
        // Arrange: the project cap is one byte short of the new item while the large item is on disk
        var onDisk = Directory.EnumerateFiles(_tree.Full("archive/zyggy")).Sum(f => new FileInfo(f).Length);
        var cap = onDisk + new FileInfo(_source).Length - 1;
        File.WriteAllText(_environment["ZYGGY_ARCHIVE_CONFIG"]!, string.Create(CultureInfo.InvariantCulture, $$"""{"item_max_bytes": 1000, "project_max_bytes": {{cap}}}"""));

        // Act
        var (before, beforeConsole) = await AddAsync("fence-2026");
        File.Delete(_tree.Full("archive/zyggy/plan.txt"));
        var (after, afterConsole) = await AddAsync("fence-2026");

        // Assert
        before.Should().Be(ArchiveVerb.Refused);
        beforeConsole.Stderr.Should().Be("archive: refused: project_cap\n");
        after.Should().Be(ArchiveVerb.Archived, afterConsole.Stderr);
    }

    [Fact]
    public async Task Add_OffloadedSlug_SlugTaken()
    {
        // Arrange: the item's bytes are gone; only the offloaded sidecar remains
        File.Delete(_tree.Full("archive/zyggy/plan.txt"));

        // Act
        var (exit, console) = await AddAsync("plan");

        // Assert
        exit.Should().Be(ArchiveVerb.Refused);
        console.Stderr.Should().Be("archive: refused: slug_taken\n");
    }

    private async Task<(int Exit, VerbConsole Console)> AddAsync(string slug)
    {
        var console = new VerbConsole();
        string[] args = ["add", "--project", "zyggy", "--slug", slug, "--name", "Fence quote", "--description", "Garden fence quote", "--file", _source];
        var exit = await new ArchiveVerb(_environment, _clock, FindTimeZone, _git).RunAsync(args, console.Io, TestContext.Current.CancellationToken);
        return (exit, console);
    }

    private static TimeZoneInfo FindTimeZone(string id) => TimeZoneInfo.CreateCustomTimeZone(id, TimeSpan.FromHours(2), id, id);
}
