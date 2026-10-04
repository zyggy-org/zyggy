using Zyggy.Core.Dream;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Dream;

/// <summary>AC-9: [stated] inbox lines (oldest file first), then daily lines, then [observed] inbox lines; caps; ledger.</summary>
public sealed class BatchPlannerTests
{
    private static readonly HashSet<string> NoQuarantine = [];

    private static DreamBatch? Plan(MemoryTree tree, DreamLedger? ledger = null, IReadOnlySet<string>? quarantine = null, int maxLines = 150,
        int maxBytes = 60_000) =>
        BatchPlanner.Plan(MemorySnapshot.Load(tree.Paths), ledger ?? DreamLedger.Empty(), quarantine ?? NoQuarantine, maxLines, maxBytes);

    private static List<string> Texts(DreamBatch? batch) => batch!.Lines.Select(l => l.Text).ToList();

    [Fact]
    public void Plan_StatedThenDailyThenObserved_InOrder()
    {
        // Arrange
        using var tree = new MemoryTree(
            ("inbox/m365-mail-backfill-2026-09-01.md", "- [observed] 2026-09-01 [m365-mail 2026-09-01]: O1\n"),
            ("inbox/remember-2026-09-29.md", "- [stated] 2026-09-29: S1\n"),
            ("daily/2026-09-30.md", "- [observed] 09:15 session abc: D1\n"));

        // Act
        var batch = Plan(tree);

        // Assert
        Texts(batch).Should().Equal(
            "- [stated] 2026-09-29: S1",
            "- [observed] 09:15 session abc: D1",
            "- [observed] 2026-09-01 [m365-mail 2026-09-01]: O1");
        batch!.Lines.Select(l => l.Class).Should().Equal(DreamLineClass.StatedInbox, DreamLineClass.Daily, DreamLineClass.ObservedInbox);
    }

    [Fact]
    public void Plan_ObservedOrderedByFileDateThenNameThenLine()
    {
        // Arrange
        using var tree = new MemoryTree(
            ("inbox/zz-2026-09-02.md", "- [observed] 2026-09-02 [x 2026-09-02]: C\n"),
            ("inbox/b-backfill-2026-09-01.md", "- [observed] 2026-09-01 [x 2026-09-01]: B1\n- [observed] 2026-09-01 [x 2026-09-01]: B2\n"),
            ("inbox/a-backfill-2026-09-01.md", "- [observed] 2026-09-01 [x 2026-09-01]: A\n"),
            ("inbox/context-01J8Y.md", "- [observed] 2026-08-01 [x 2026-08-01]: NoDate\n"));

        // Act
        var batch = Plan(tree);

        // Assert
        Texts(batch).Select(t => t[(t.LastIndexOf(':') + 2)..]).Should().Equal("A", "B1", "B2", "C", "NoDate");
    }

    [Fact]
    public void Plan_StatedInOlderFileFirst()
    {
        // Arrange
        using var tree = new MemoryTree(
            ("inbox/remember-2026-10-02.md", "- [stated] 2026-10-02: newer\n"),
            ("inbox/remember-2026-09-30.md", "- [stated] 2026-09-30: older\n"));

        // Act
        var batch = Plan(tree);

        // Assert
        Texts(batch).Should().Equal("- [stated] 2026-09-30: older", "- [stated] 2026-10-02: newer");
    }

    [Fact]
    public void Plan_ConsumedLinesNeverOffered()
    {
        // Arrange
        using var tree = new MemoryTree(("inbox/remember-2026-09-30.md", "- [stated] 2026-09-30: one\n- [stated] 2026-09-30: two\n"));
        var ledger = DreamLedger.Empty();
        ledger.Consume("inbox/remember-2026-09-30.md", [LineHash.Of("- [stated] 2026-09-30: one")], new DateOnly(2026, 10, 1));

        // Act
        var batch = Plan(tree, ledger);

        // Assert
        Texts(batch).Should().Equal("- [stated] 2026-09-30: two");
    }

    [Fact]
    public void Plan_QuarantinedLinesNeverOffered()
    {
        // Arrange
        using var tree = new MemoryTree(("inbox/remember-2026-09-30.md", "- [stated] 2026-09-30: bad\n- [stated] 2026-09-30: good\n"));

        // Act
        var batch = Plan(tree, quarantine: new HashSet<string> { LineHash.Of("- [stated] 2026-09-30: bad") });

        // Assert
        Texts(batch).Should().Equal("- [stated] 2026-09-30: good");
    }

    [Fact]
    public void Plan_StopsBeforeMaxLines()
    {
        // Arrange
        using var tree = new MemoryTree(("inbox/remember-2026-09-30.md",
            string.Concat(Enumerable.Range(1, 20).Select(i => $"- [stated] 2026-09-30: fact {i}\n"))));

        // Act
        var batch = Plan(tree, maxLines: 7);

        // Assert
        batch!.Lines.Should().HaveCount(7);
        batch.Lines[^1].Text.Should().EndWith("fact 7");
    }

    [Fact]
    public void Plan_StopsBeforeMaxBytes()
    {
        // Arrange: each line is 30 bytes + newline.
        using var tree = new MemoryTree(("inbox/remember-2026-09-30.md",
            string.Concat(Enumerable.Range(1, 9).Select(i => $"- [stated] 2026-09-30: fact {i}\n"))));

        // Act
        var batch = Plan(tree, maxBytes: 100);

        // Assert
        batch!.Lines.Should().HaveCount(3);
    }

    [Fact]
    public void Plan_FirstLineOverMaxBytes_OfferedAlone()
    {
        // Arrange
        using var tree = new MemoryTree(("inbox/remember-2026-09-30.md", $"- [stated] 2026-09-30: {new string('x', 200)}\n- [stated] 2026-09-30: small\n"));

        // Act
        var batch = Plan(tree, maxBytes: 100);

        // Assert
        batch!.Lines.Should().ContainSingle();
    }

    [Fact]
    public void Plan_IdenticalLinesInOneFileOfferedOnce()
    {
        // Arrange
        using var tree = new MemoryTree(("inbox/remember-2026-09-30.md", "- [stated] 2026-09-30: same\n- [stated] 2026-09-30: same  \n"));

        // Act
        var batch = Plan(tree);

        // Assert
        batch!.Lines.Should().ContainSingle();
    }

    [Fact]
    public void Plan_FrontMatterHeadingsAndBlankLinesIgnored()
    {
        // Arrange
        using var tree = new MemoryTree(("inbox/m365-mail-backfill-2026-10-03.md",
            "---\nname: backfill\ndescription: mail facts\n---\n# Mail backfill\n\n- [observed] 2026-10-03 [m365-mail 2026-10-03]: fact\n\n## more\n"));

        // Act
        var batch = Plan(tree);

        // Assert
        Texts(batch).Should().Equal("- [observed] 2026-10-03 [m365-mail 2026-10-03]: fact");
        batch!.Lines[0].LineNumber.Should().Be(7);
        batch.Lines[0].FileDate.Should().Be(new DateOnly(2026, 10, 3));
    }

    [Fact]
    public void Plan_MonthlyDailyArchiveAndUnderscoreFilesNotOffered()
    {
        // Arrange
        using var tree = new MemoryTree(
            ("daily/2026-08.md", "## 2026-08-01\n- [observed] 09:15 session a: archived\n"),
            ("inbox/_draft.md", "- [stated] 2026-09-30: underscore\n"),
            ("daily/notes.md", "- [observed] 09:15 session a: not a day file\n"),
            ("private/people/carol.md", "- [stated] 2026-09-30: durable\n"));

        // Act
        var batch = Plan(tree);

        // Assert
        batch.Should().BeNull();
    }

    [Fact]
    public void Plan_NothingUnconsumed_ReturnsNull()
    {
        // Arrange
        using var tree = new MemoryTree(("inbox/remember-2026-09-30.md", "- [stated] 2026-09-30: one\n"));
        var ledger = DreamLedger.Empty();
        ledger.Consume("inbox/remember-2026-09-30.md", [LineHash.Of("- [stated] 2026-09-30: one")], new DateOnly(2026, 10, 1));

        // Act
        var batch = Plan(tree, ledger);

        // Assert
        batch.Should().BeNull();
    }

    [Fact]
    public void Plan_IdsAreL1ToLnInBatchOrder()
    {
        // Arrange
        using var tree = new MemoryTree(
            ("inbox/remember-2026-09-30.md", "- [stated] 2026-09-30: a\n- [stated] 2026-09-30: b\n"),
            ("daily/2026-09-30.md", "- [observed] 10:00 session s: c\n"));

        // Act
        var batch = Plan(tree);

        // Assert
        batch!.Lines.Select(l => l.Id).Should().Equal("L1", "L2", "L3");
        batch.Lines[0].Hash.Should().Be(LineHash.Of("- [stated] 2026-09-30: a"));
        batch.Lines[2].RelativePath.Should().Be("daily/2026-09-30.md");
    }
}
