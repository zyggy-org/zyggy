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

    // Spec 13 decision Q2 (AC-27): the not-yet-filed lines of inbox/bus-*.md and inbox/remember-*.md, and nothing else of the inbox.
    [Fact]
    public void PickedUpLines_BusAndRememberFiles_Only_BackfillAndOthersNever()
    {
        // Arrange
        using var tree = new MemoryTree(
            ("inbox/bus-context-home-laptop-01K8ZX3P4Q5R6S7T8V9W0XYZAB-2026-10-27.md", "---\nname: bus context x\n---\n- context line\n"),
            ("inbox/bus-report-home-laptop-01K8ZX2A3B4C5D6E7F8G9H0JKM-01K8ZX4Q5R6S7T8V9W0XYZAC-2026-10-27.md", "---\nname: bus report x\n---\n- report line\n"),
            ("inbox/remember-2026-10-27.md", "- remembered line\n"),
            ("inbox/files-2026-10-27.md", "- files line\n"),
            ("inbox/m365-mail-backfill-2026-10-27.md", "- backfill line\n"),
            ("inbox/github-inventory-2026-10-27.md", "- github line\n"),
            ("inbox/_bus-hidden-2026-10-27.md", "- hidden line\n"));

        // Act
        var lines = new MemoryStore(tree.Paths).PickedUpLines(null, new ContextOptions());

        // Assert
        lines.Select(line => line.Text).Should().BeEquivalentTo("- context line", "- report line", "- remembered line");
    }

    [Fact]
    public void PickedUpLines_LedgerConsumedLines_Skipped()
    {
        // Arrange
        using var tree = new MemoryTree(("inbox/remember-2026-10-27.md", "- filed\n- not yet\n"));
        var ledger = Zyggy.Core.Dream.DreamLedger.Empty();
        ledger.Consume("inbox/remember-2026-10-27.md", [Zyggy.Core.Dream.LineHash.Of("- filed")], Today);

        // Act
        var lines = new MemoryStore(tree.Paths).PickedUpLines(ledger, new ContextOptions());

        // Assert
        lines.Select(line => line.Text).Should().Equal("- not yet");
    }

    [Fact]
    public void PickedUpLines_LedgerNull_NothingConsumed()
    {
        // Arrange
        using var tree = new MemoryTree(("inbox/remember-2026-10-27.md", "- one\n- two\n"));

        // Act
        var lines = new MemoryStore(tree.Paths).PickedUpLines(null, new ContextOptions());

        // Assert
        lines.Should().HaveCount(2);
    }

    [Fact]
    public void PickedUpLines_NewestFileFirst_FileOrderKept()
    {
        // Arrange
        using var tree = new MemoryTree(
            ("inbox/remember-2026-10-25.md", "- old one\n- old two\n"),
            ("inbox/bus-context-home-laptop-01K8ZX3P4Q5R6S7T8V9W0XYZAB-2026-10-27.md", "- new one\n- new two\n"),
            ("inbox/remember-2026-10-27.md", "- remember new\n"));

        // Act
        var lines = new MemoryStore(tree.Paths).PickedUpLines(null, new ContextOptions());

        // Assert: by the file-name date descending, then the file name descending, lines in file order
        lines.Select(line => line.Text).Should().Equal("- remember new", "- new one", "- new two", "- old one", "- old two");
        lines[0].FileDate.Should().Be(new DateOnly(2026, 10, 27));
    }

    [Fact]
    public void PickedUpLines_FrontMatterAndNonDashLines_NotCandidates()
    {
        // Arrange
        using var tree = new MemoryTree(("inbox/remember-2026-10-27.md", "---\nname: x\ndescription: - not a line\n---\nprose\n\n- a line\n"));

        // Act
        var lines = new MemoryStore(tree.Paths).PickedUpLines(null, new ContextOptions());

        // Assert
        lines.Select(line => line.Text).Should().Equal("- a line");
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "Symbolic links need Linux (as MemoryPathsSymlinkTests).")]
    public void PickedUpLines_SymlinkOutOfPrincipal_Skipped()
    {
        // Arrange
        using var tree = new MemoryTree(("inbox/remember-2026-10-27.md", "- inside\n"));
        var outside = Path.Combine(tree.Root, "outside.md");
        File.WriteAllText(outside, "- outside\n");
        File.CreateSymbolicLink(tree.Full("inbox/bus-context-x-2026-10-27.md"), outside);

        // Act
        var lines = new MemoryStore(tree.Paths).PickedUpLines(null, new ContextOptions());

        // Assert
        lines.Select(line => line.Text).Should().Equal("- inside");
    }

    [Fact]
    public void Ledger_InvalidOrUnsupportedJson_Null_MissingFile_Empty()
    {
        // Arrange
        using var missing = new MemoryTree();
        using var invalid = new MemoryTree((".dream/ledger.json", "{}"));
        using var unsupported = new MemoryTree((".dream/ledger.json", "{\"schema\": 99, \"files\": {}}"));

        // Act + Assert
        new MemoryStore(missing.Paths).Ledger().Should().NotBeNull().And.Match<Zyggy.Core.Dream.DreamLedger>(l => l.Files.Count == 0);
        new MemoryStore(invalid.Paths).Ledger().Should().BeNull();
        new MemoryStore(unsupported.Paths).Ledger().Should().BeNull();
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
