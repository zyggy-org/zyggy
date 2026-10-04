using System.Text.Json;

using Zyggy.Core.Dream;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Dream;

public sealed class DreamWriterTests
{
    private static (MemoryTree Tree, WorkingSet Set) Prepared()
    {
        var tree = new MemoryTree(
            ("private/people/carol.md", "- [stated] 2026-09-28: Carol is my sister.\n"),
            ("private/people/old.md", "- [stated] 2026-09-28: old.\n"));
        var set = new WorkingSet(MemorySnapshot.Load(tree.Paths));
        set.Write("private/people/carol.md", "- [stated] 2026-09-28: Carol is my sister.\n- [stated] 2026-09-29: Carol likes tea.\n");
        set.Write("business/clients/acme.md", "- [observed] 2026-09-29 [m365-mail 2026-09-29]: Acme.\n");
        set.Delete("private/people/old.md");
        set.Write(".dream/ledger.json", "{\"schema\": 1, \"files\": {}}\n");
        return (tree, set);
    }

    [Fact]
    public void Write_MarkerListsEveryPathWithWrittenAndBeforeHashes()
    {
        // Arrange
        var (tree, set) = Prepared();
        using var _ = tree;
        string? marker = null;
        var writer = new DreamWriter(path => marker ??= File.Exists(tree.Paths.Pending) ? File.ReadAllText(tree.Paths.Pending) : null);

        // Act
        writer.Write(tree.Paths, set, "01JRUN");

        // Assert
        using var json = JsonDocument.Parse(marker!);
        json.RootElement.GetProperty("run").GetString().Should().Be("01JRUN");
        var files = json.RootElement.GetProperty("files").EnumerateArray().ToDictionary(f => f.GetProperty("path").GetString()!);
        files.Keys.Should().BeEquivalentTo("private/people/carol.md", "business/clients/acme.md", "private/people/old.md", ".dream/ledger.json");
        files["private/people/carol.md"].GetProperty("existed_before").GetBoolean().Should().BeTrue();
        files["private/people/carol.md"].GetProperty("sha256_before").GetString().Should().HaveLength(64);
        files["private/people/carol.md"].GetProperty("sha256_written").GetString().Should().HaveLength(64);
        files["business/clients/acme.md"].GetProperty("existed_before").GetBoolean().Should().BeFalse();
        files["business/clients/acme.md"].GetProperty("sha256_before").ValueKind.Should().Be(JsonValueKind.Null);
        files["private/people/old.md"].GetProperty("sha256_written").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void Write_LedgerWrittenAfterFiles()
    {
        // Arrange
        var (tree, set) = Prepared();
        using var _ = tree;
        var order = new List<string>();
        var writer = new DreamWriter(order.Add);

        // Act
        writer.Write(tree.Paths, set, "01JRUN");

        // Assert
        order[0].Should().Be(".dream/pending.json");
        order[^1].Should().Be(".dream/ledger.json");
        order.Should().HaveCount(5);
        File.Exists(tree.Full("private/people/old.md")).Should().BeFalse();
        File.ReadAllText(tree.Full("private/people/carol.md")).Should().Contain("Carol likes tea.");
    }

    [Fact]
    public void Write_AtomicNoTempLeft()
    {
        // Arrange
        var (tree, set) = Prepared();
        using var _ = tree;

        // Act
        new DreamWriter().Write(tree.Paths, set, "01JRUN");
        DreamWriter.DeleteMarker(tree.Paths);

        // Assert
        Directory.EnumerateFiles(tree.Root, "*", SearchOption.AllDirectories).Should().NotContain(f => f.Contains(".zyggy-tmp-", StringComparison.Ordinal));
        File.Exists(tree.Paths.Pending).Should().BeFalse();
    }
}
