using Zyggy.Core.Memory;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Memory;

public sealed class MemoryStoreTests
{
    private static readonly DateOnly Today = new(2026, 10, 28);

    public static bool IsLinux => OperatingSystem.IsLinux();

    [Fact]
    public void DurableEntries_SidedGolden_OnlySlugFilesOnBothSides_IndexAndUnderscoreExcluded()
    {
        // Arrange
        using var tree = MemoryTree.CopyOf(Path.Combine("digest", "sided"));

        // Act
        var entries = new MemoryStore(tree.Paths).DurableEntries();

        // Assert
        entries.Select(entry => entry.RelativePath).Should().Equal(
            "business/areas/zyggy.md",
            "business/clients/acme-corp.md",
            "business/clients/globex.md",
            "private/areas/marathon.md",
            "private/people/carol.md");
        entries[0].Side.Should().Be(MemorySide.Business);
        entries[0].Category.Value.Should().Be("areas");
        entries[0].Slug.Value.Should().Be("zyggy");
        entries[0].File.Description.Should().Be("Zyggy, the platform Alice builds");
    }

    [Fact]
    public void DurableEntries_InboxAutoArchiveDreamLegacyAndIdentityFiles_NeverReturned()
    {
        // Arrange — every excluded place holds the topic word; only one real durable file exists.
        using var tree = new MemoryTree(
            ("inbox/x.md", "- zyggy\n"),
            ("auto/MEMORY.md", "- zyggy\n"),
            ("archive/zyggy/quote.md", "---\nname: zyggy\n---\nzyggy\n"),
            (".dream/quarantine.md", "- zyggy\n"),
            ("areas/old.md", "---\nname: zyggy\n---\nzyggy\n"),
            ("people/bob.md", "---\nname: zyggy\n---\nzyggy\n"),
            ("profile.md", "zyggy\n"),
            ("preferences.md", "zyggy\n"),
            ("agents.md", "zyggy\n"),
            ("private/people/carol.md", "---\nname: carol\n---\n- zyggy\n"));

        // Act
        var entries = new MemoryStore(tree.Paths).DurableEntries();

        // Assert
        entries.Select(entry => entry.RelativePath).Should().Equal("private/people/carol.md");
    }

    [Fact]
    public void DurableEntries_UnparsableFrontMatter_EntryWithEmptyFrontMatterNeverThrows()
    {
        // Arrange — the front matter never closes.
        using var tree = new MemoryTree(("business/areas/broken.md", "---\nname: [unterminated\n"));

        // Act
        var entries = new MemoryStore(tree.Paths).DurableEntries();

        // Assert
        var entry = entries.Should().ContainSingle().Subject;
        entry.File.Name.Should().BeNull();
        entry.File.Aliases.Should().BeEmpty();
        entry.File.Updated.Should().BeNull();
        var (normalised, terms, _) = ContextRanker.Topic("broken", new ContextOptions());
        ContextRanker.Match(normalised, terms, entry).Should().Be(MatchTier.Exact);
    }

    [Fact]
    public void DurableEntries_FrontMatterFields_ReadThroughMemoryFileReader()
    {
        // Arrange — a flow-list alias and a block-sequence alias.
        using var tree = new MemoryTree(
            ("business/areas/zyggy.md", "---\nname: Zyggy\naliases: [zy, bus]\ndescription: The platform\nupdated: 2026-10-20\n---\nbody\n"),
            ("private/people/carol.md", "---\nname: Carol\naliases:\n  - sis\n  - caro\n---\nbody\n"));

        // Act
        var entries = new MemoryStore(tree.Paths).DurableEntries();

        // Assert
        entries[0].File.Name.Should().Be("Zyggy");
        entries[0].File.Aliases.Should().Equal("zy", "bus");
        entries[0].File.Description.Should().Be("The platform");
        entries[0].File.Updated.Should().Be(new DateOnly(2026, 10, 20));
        entries[0].File.BodyLines.Should().Equal("body");
        entries[1].File.Aliases.Should().Equal("sis", "caro");
        entries[1].File.Description.Should().BeNull();
    }

    [Fact]
    public void DailyLines_WindowByFileNameDate_NewestFirst_BodyLinesOnly_EmptyLinesSkipped()
    {
        // Arrange — today, yesterday (with a front matter), today − 30 (inside), today − 31 (outside).
        using var tree = new MemoryTree(
            ("daily/2026-10-28.md", "- today a\n\n- today b\n"),
            ("daily/2026-10-27.md", "---\nupdated: 2026-10-27\n---\n- yesterday\n"),
            ("daily/2026-09-28.md", "- edge of the window\n"),
            ("daily/2026-09-27.md", "- too old\n"));

        // Act
        var lines = new MemoryStore(tree.Paths).DailyLines(Today, 30);

        // Assert
        lines.Should().Equal(
            new DailyLine(new DateOnly(2026, 10, 28), "- today a"),
            new DailyLine(new DateOnly(2026, 10, 28), "- today b"),
            new DailyLine(new DateOnly(2026, 10, 27), "- yesterday"),
            new DailyLine(new DateOnly(2026, 9, 28), "- edge of the window"));
    }

    [Fact]
    public void DailyLines_MonthlyRollupAndNotesMd_ContributeNothing()
    {
        // Arrange
        using var tree = new MemoryTree(
            ("daily/2026-10.md", "- roof roll-up\n"),
            ("daily/notes.md", "- roof notes\n"));

        // Act
        var lines = new MemoryStore(tree.Paths).DailyLines(Today, 30);

        // Assert
        lines.Should().BeEmpty();
    }

    [Fact]
    public void DailyLines_DirectoryAbsent_Empty()
    {
        // Arrange
        using var tree = new MemoryTree();

        // Act
        var store = new MemoryStore(tree.Paths);

        // Assert
        store.DailyLines(Today, 30).Should().BeEmpty();
        store.DurableEntries().Should().BeEmpty();
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "Symbolic links need Linux (as MemoryPathsSymlinkTests).")]
    public void DurableEntries_SymlinkLeadingOutside_Skipped()
    {
        // Arrange — a category directory that is a link to a directory outside the principal's tree.
        using var tree = new MemoryTree(("private/people/carol.md", "---\nname: carol\n---\nbody\n"));
        var outside = Path.Combine(tree.Root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "evil.md"), "---\nname: evil\n---\nbody\n");
        Directory.CreateSymbolicLink(tree.Full("private/linked"), outside);

        // Act
        var entries = new MemoryStore(tree.Paths).DurableEntries();

        // Assert
        entries.Select(entry => entry.RelativePath).Should().Equal("private/people/carol.md");
    }
}
