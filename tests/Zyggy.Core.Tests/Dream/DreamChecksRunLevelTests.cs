using Zyggy.Core.Dream;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Dream;

/// <summary>AC-17: the run-level breaker refuses a run whose accepted batches together remove too much durable memory.</summary>
public sealed class DreamChecksRunLevelTests
{
    private static MemoryTree Tree() => new(
        ("private/areas/a.md", string.Concat(Enumerable.Range(1, 10).Select(i => $"- [stated] 2026-09-18: a {i}.\n"))),
        ("business/areas/b.md", string.Concat(Enumerable.Range(1, 10).Select(i => $"- [stated] 2026-09-18: b {i}.\n"))),
        ("inbox/x.md", string.Concat(Enumerable.Range(1, 50).Select(i => $"- [stated] 2026-09-18: inbox {i}.\n"))));

    private static WorkingSet RemoveLines(MemoryTree tree, int fromA, int fromB)
    {
        var snapshot = MemorySnapshot.Load(tree.Paths);
        var set = new WorkingSet(snapshot);
        set.Write("private/areas/a.md", string.Concat(Enumerable.Range(1 + fromA, 10 - fromA).Select(i => $"- [stated] 2026-09-18: a {i}.\n")));
        set.Write("business/areas/b.md", string.Concat(Enumerable.Range(1 + fromB, 10 - fromB).Select(i => $"- [stated] 2026-09-18: b {i}.\n")));
        return set;
    }

    [Fact]
    public void CheckRun_RemovalsOverTenPercentOfDurableLines_RunRemovalLimit()
    {
        // Arrange: 3 of 20 durable lines (15 %); inbox lines do not count.
        using var tree = Tree();
        var set = RemoveLines(tree, 2, 1);

        // Act
        var check = DreamChecks.CheckRun(set.Snapshot, set, new DreamOptions());

        // Assert
        check.Should().Be(DreamCheck.RunRemovalLimit);
    }

    [Fact]
    public void CheckRun_ArchiveSidecarLines_NotCountedAsDurable()
    {
        // Arrange: 3 of 20 durable lines removed (15 %); 100 "- " lines in a sidecar would dilute it to 2.5 % if they counted.
        using var tree = Tree();
        tree.Write("archive/zyggy/long.md", "---\nname: Long\ndescription: long\nupdated: 2026-09-30\n---\n"
            + string.Concat(Enumerable.Range(1, 100).Select(i => $"- [stated] 2026-09-18: sidecar {i}.\n")));
        var set = RemoveLines(tree, 2, 1);

        // Act
        var check = DreamChecks.CheckRun(set.Snapshot, set, new DreamOptions());

        // Assert
        check.Should().Be(DreamCheck.RunRemovalLimit);
    }

    [Fact]
    public void CheckRun_AtLimit_Passes()
    {
        // Arrange: 2 of 20 durable lines (10 %).
        using var tree = Tree();
        var set = RemoveLines(tree, 1, 1);

        // Act
        var check = DreamChecks.CheckRun(set.Snapshot, set, new DreamOptions());

        // Assert
        check.Should().BeNull();
    }
}
