using System.Text;

using Zyggy.Core.Memory;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Memory;

/// <summary>
/// Spec 38 AC-1, AC-2: exactly the indexed items with bytes present, a parsed sidecar without an offload part, not parked, not excluded and
/// at least <c>min_bytes</c> — interrupted offloads first, ordinal order, within the per-run caps; no item byte read until
/// <see cref="OffloadSelection.ReadAndCheck"/>, which refuses a SHA-256 mismatch.
/// </summary>
public sealed class OffloadSelectionTests : IDisposable
{
    private const string TextItem = "Quote for the Zyggy roof repair.\nTotal 1 234,00 EUR, valid 30 days.\n";
    private static readonly Slug Zyggy = Slug.Parse("zyggy");

    private readonly MemoryTree _tree = new();
    private readonly OffloadState _state = new();

    public void Dispose() => _tree.Dispose();

    [Fact]
    public void Select_EveryState_OnlyIndexedPresentParsedNotOffloadedNotParkedNotExcludedAboveMin()
    {
        // Arrange
        var rows = new[]
        {
            Item("a-eligible"),
            Item("b-unindexed", state: ArchiveIndexState.Unindexed),
            ItemOnly("c-orphan"),
            Offloaded("d-offloaded"),
            Item("e-parked"),
            Item("f-excluded"),
            Item("g-small", bytes: 10),
        };
        _state.Park("zyggy/e-parked", OffloadReason.TargetConflict, new DateOnly(2026, 10, 1));
        var options = new OffloadOptions { MinBytes = 50, Exclude = ["zyggy/f-excluded"] };

        // Act
        var plan = OffloadSelection.Select(rows, _tree.Paths, options, _state);

        // Assert
        plan.Candidates.Select(c => c.Slug.Value).Should().Equal("a-eligible");
        plan.Deferred.Should().BeEmpty();
        plan.Candidates[0].Should().BeEquivalentTo(new { Size = 68L, Resume = false, ItemPath = _tree.Full("archive/zyggy/a-eligible.txt") });
    }

    [Fact]
    public void Select_OrdinalOrder()
    {
        // Arrange
        var rows = new[] { Item("c"), Item("a-2"), Item("b"), Item("a") };

        // Act
        var plan = OffloadSelection.Select(rows, _tree.Paths, new OffloadOptions(), _state);

        // Assert: ordinal path order, so archive/zyggy/a-2.txt ('-') sorts before archive/zyggy/a.txt ('.')
        plan.Candidates.Select(c => c.Slug.Value).Should().Equal("a-2", "a", "b", "c");
    }

    [Fact]
    public void Select_MaxItems_RestDeferred()
    {
        // Arrange
        var rows = new[] { Item("a"), Item("b"), Item("c") };

        // Act
        var plan = OffloadSelection.Select(rows, _tree.Paths, new OffloadOptions { MaxItemsPerRun = 2 }, _state);

        // Assert
        plan.Candidates.Select(c => c.Slug.Value).Should().Equal("a", "b");
        plan.Deferred.Select(c => c.Slug.Value).Should().Equal("c");
    }

    [Fact]
    public void Select_MaxBytes_RestDeferred_FirstAlwaysTaken()
    {
        // Arrange: the first item alone is over the cap; it still moves, alone
        var rows = new[] { Item("a", bytes: 2_000_000), Item("b"), Item("c") };

        // Act
        var plan = OffloadSelection.Select(rows, _tree.Paths, new OffloadOptions { MaxBytesPerRun = 1_048_576 }, _state);

        // Assert
        plan.Candidates.Select(c => c.Slug.Value).Should().Equal("a");
        plan.Deferred.Select(c => c.Slug.Value).Should().Equal("b", "c");
    }

    [Fact]
    public void Select_MaxBytes_TakesWhileWithinCap()
    {
        // Arrange
        var rows = new[] { Item("a", bytes: 600_000), Item("b", bytes: 400_000), Item("c", bytes: 100_000) };

        // Act
        var plan = OffloadSelection.Select(rows, _tree.Paths, new OffloadOptions { MaxBytesPerRun = 1_048_576 }, _state);

        // Assert
        plan.Candidates.Select(c => c.Slug.Value).Should().Equal("a", "b");
        plan.Deferred.Select(c => c.Slug.Value).Should().Equal("c");
    }

    [Fact]
    public void Select_ResumeRowsFirst()
    {
        // Arrange: z's sidecar already carries the offload part, its item is still in git (an interrupted commit)
        var rows = new[] { Item("a"), Item("z", state: ArchiveIndexState.Unindexed, sidecar: Golden("offload/sidecar-offloaded.md")) };

        // Act
        var plan = OffloadSelection.Select(rows, _tree.Paths, new OffloadOptions(), _state);

        // Assert
        plan.Candidates.Select(c => (c.Slug.Value, c.Resume)).Should().Equal(("z", true), ("a", false));
    }

    [Fact]
    public void Select_NeverReadsItemBytes()
    {
        // Arrange: the item cannot be opened for reading — selection only looks at its length
        var rows = new[] { Item("a") };
        var path = _tree.Full("archive/zyggy/a.txt");
        using var locked = OperatingSystem.IsWindows() ? new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.None);
        }

        // Act
        var plan = OffloadSelection.Select(rows, _tree.Paths, new OffloadOptions(), _state);

        // Assert
        plan.Candidates.Should().ContainSingle().Which.Size.Should().Be(68);
    }

    [Fact]
    public void ReadAndCheck_ShaMatches_Bytes()
    {
        // Arrange
        var candidate = OffloadSelection.Select([Item("a")], _tree.Paths, new OffloadOptions(), _state).Candidates[0];

        // Act
        var (bytes, reason) = OffloadSelection.ReadAndCheck(candidate);

        // Assert
        reason.Should().BeNull();
        bytes.Should().Equal(Encoding.UTF8.GetBytes(TextItem));
    }

    [Fact]
    public void ReadAndCheck_ShaMismatch_ShaMismatchNoBytes()
    {
        // Arrange: the bytes changed after the sidecar was written
        var candidate = OffloadSelection.Select([Item("a")], _tree.Paths, new OffloadOptions(), _state).Candidates[0];
        _tree.Write("archive/zyggy/a.txt", TextItem.Replace("1 234", "9 999", StringComparison.Ordinal));

        // Act
        var (bytes, reason) = OffloadSelection.ReadAndCheck(candidate);

        // Assert
        reason.Should().Be(OffloadReason.ShaMismatch);
        bytes.Should().BeNull();
    }

    private static string Golden(string name) => Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(Infrastructure.Golden.Directory, "archive", name)));

    // An item and its sidecar (37's text sidecar unless given); the row as ArchiveService.List gives it.
    private ArchiveListRow Item(string slug, int? bytes = null, ArchiveIndexState state = ArchiveIndexState.Indexed, string? sidecar = null)
    {
        _tree.Write($"archive/zyggy/{slug}.txt", bytes is { } n ? new string('x', n) : TextItem);
        _tree.Write($"archive/zyggy/{slug}.md", sidecar ?? Golden("sidecar-text.md"));
        return Row(slug, $"archive/zyggy/{slug}.txt", $"archive/zyggy/{slug}.md", state);
    }

    private ArchiveListRow ItemOnly(string slug)
    {
        _tree.Write($"archive/zyggy/{slug}.txt", TextItem);
        return Row(slug, $"archive/zyggy/{slug}.txt", null, ArchiveIndexState.Orphan);
    }

    private ArchiveListRow Offloaded(string slug)
    {
        _tree.Write($"archive/zyggy/{slug}.md", Golden("offload/sidecar-offloaded.md"));
        return Row(slug, $"archive/zyggy/{slug}.txt", $"archive/zyggy/{slug}.md", ArchiveIndexState.Offloaded);
    }

    private static ArchiveListRow Row(string slug, string? item, string? sidecar, ArchiveIndexState state) =>
        new(item, sidecar, Zyggy, Slug.Parse(slug), ArchiveMediaType.TextPlain, 68, "Quote 2026", "Roof repair quote", new DateOnly(2026, 9, 30), state);
}
