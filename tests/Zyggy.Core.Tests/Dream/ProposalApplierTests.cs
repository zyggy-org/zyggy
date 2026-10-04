using System.Text;

using Zyggy.Core.Dream;
using Zyggy.Core.Memory;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Dream;

/// <summary>AC-14, AC-18 (unit): .NET applies a valid proposal in memory; the model never writes a file.</summary>
public sealed class ProposalApplierTests
{
    private static readonly DateOnly RunDate = new(2026, 10, 4);

    private const string Carol = "---\nname: carol\ndescription: Carol, the sister of Alice\nupdated: 2026-09-28\n---\n" +
        "- [stated] 2026-09-28: Carol is my sister.\n- [stated] 2026-09-28: Carol lives in Leuven.\n";

    private static MemoryTree Tree() => new(
        ("private/people/_index.md", "---\nname: people\ndescription: People\nupdated: 2026-09-28\n---\n"),
        ("business/areas/_index.md", "---\nname: areas\ndescription: Projects\nupdated: 2026-09-28\n---\n"),
        ("private/people/carol.md", Carol));

    private static (WorkingSet Set, ApplyResult Result) Apply(MemoryTree tree, TestProposals proposal)
    {
        var snapshot = MemorySnapshot.Load(tree.Paths);
        var set = new WorkingSet(snapshot);
        var parsed = DreamProposalParser.Parse(proposal.Build())!;
        return (set, ProposalApplier.Apply(parsed, snapshot, set, RunDate));
    }

    private static MemoryFile Read(WorkingSet set, string path) => MemoryFileReader.Parse(set.Text(path)!);

    [Fact]
    public void Apply_Create_NewFileWithFrontMatterAndLines()
    {
        // Arrange
        using var tree = Tree();
        var proposal = TestProposals.New().Create("business/areas/zyggy.md", "Zyggy", "The agent platform",
            ["- [observed] 2026-09-29 [github-inventory 2026-09-29]: Repository zyggy exists."], ["zyggy-platform"]);

        // Act
        var (set, result) = Apply(tree, proposal);

        // Assert
        var file = Read(set, "business/areas/zyggy.md");
        file.Name.Should().Be("Zyggy");
        file.Description.Should().Be("The agent platform");
        file.Aliases.Should().Equal("zyggy-platform");
        file.Updated.Should().Be(RunDate);
        file.BodyLines.Should().Equal("- [observed] 2026-09-29 [github-inventory 2026-09-29]: Repository zyggy exists.");
        result.FilesCreated.Should().Be(1);
        result.EditMismatch.Should().BeFalse();
    }

    [Fact]
    public void Apply_Append_AddsLinesAndSetsUpdatedToRunDate()
    {
        // Arrange
        using var tree = Tree();
        var proposal = TestProposals.New().Edit("private/people/carol.md", append: ["- [stated] 2026-09-29: Carol likes tea."]);

        // Act
        var (set, result) = Apply(tree, proposal);

        // Assert
        var file = Read(set, "private/people/carol.md");
        file.BodyLines.Should().HaveCount(3).And.EndWith("- [stated] 2026-09-29: Carol likes tea.");
        file.Updated.Should().Be(RunDate);
        result.FilesEdited.Should().Be(1);
    }

    [Fact]
    public void Apply_Replace_SwapsExactLine()
    {
        // Arrange
        using var tree = Tree();
        var proposal = TestProposals.New().Edit("private/people/carol.md",
            replace: [("- [stated] 2026-09-28: Carol lives in Leuven.", "- [stated] 2026-09-28: Carol lives in Leuven since 2020.")]);

        // Act
        var (set, _) = Apply(tree, proposal);

        // Assert
        Read(set, "private/people/carol.md").BodyLines.Should().Equal(
            "- [stated] 2026-09-28: Carol is my sister.", "- [stated] 2026-09-28: Carol lives in Leuven since 2020.");
    }

    [Fact]
    public void Apply_Remove_DropsExactLine()
    {
        // Arrange
        using var tree = Tree();
        var proposal = TestProposals.New().Edit("private/people/carol.md", remove: [("- [stated] 2026-09-28: Carol lives in Leuven.", "expired")]);

        // Act
        var (set, _) = Apply(tree, proposal);

        // Assert
        Read(set, "private/people/carol.md").BodyLines.Should().Equal("- [stated] 2026-09-28: Carol is my sister.");
    }

    [Fact]
    public void Apply_RemoveUnknownLine_ReportsEditMismatch()
    {
        // Arrange
        using var tree = Tree();
        var proposal = TestProposals.New().Edit("private/people/carol.md", remove: [("- [stated] 2026-09-28: not there", "expired")]);

        // Act
        var (_, result) = Apply(tree, proposal);

        // Assert
        result.EditMismatch.Should().BeTrue();
    }

    [Fact]
    public void Apply_NewCategory_CreatesIndexWithNameDescriptionUpdated()
    {
        // Arrange
        using var tree = Tree();
        var proposal = TestProposals.New()
            .NewCategory("business", "clients", "Companies Alice works for")
            .Create("business/clients/acme-corp.md", "Acme Corp", "A client", ["- [observed] 2026-09-29 [m365-mail 2026-09-29]: Acme renewed."]);

        // Act
        var (set, result) = Apply(tree, proposal);

        // Assert
        var index = Read(set, "business/clients/_index.md");
        index.Name.Should().Be("clients");
        index.Description.Should().Be("Companies Alice works for");
        index.Updated.Should().Be(RunDate);
        index.BodyLines.Should().BeEmpty();
        result.CategoriesCreated.Should().Be(1);
    }

    [Fact]
    public void Apply_DescriptionAndAliases_Updated()
    {
        // Arrange
        using var tree = Tree();
        var proposal = TestProposals.New().Edit("private/people/carol.md", description: "Carol, Alice's sister in Leuven", aliases: ["caro"]);

        // Act
        var (set, _) = Apply(tree, proposal);

        // Assert
        var file = Read(set, "private/people/carol.md");
        file.Description.Should().Be("Carol, Alice's sister in Leuven");
        file.Aliases.Should().Equal("caro");
        file.Updated.Should().Be(RunDate);
    }

    [Fact]
    public void Apply_NeverTouchesDisk()
    {
        // Arrange
        using var tree = Tree();
        var before = Directory.EnumerateFiles(tree.Root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, f => Convert.ToHexString(File.ReadAllBytes(f)));
        var proposal = TestProposals.New()
            .NewCategory("business", "clients", "Companies")
            .Create("business/clients/acme-corp.md", "Acme", "A client", ["- [observed] 2026-09-29 [m365-mail 2026-09-29]: Acme."])
            .Edit("private/people/carol.md", append: ["- [stated] 2026-09-29: Carol likes tea."]);

        // Act
        var (set, _) = Apply(tree, proposal);

        // Assert
        set.ChangedPaths.Should().BeEquivalentTo("business/clients/_index.md", "business/clients/acme-corp.md", "private/people/carol.md");
        Directory.EnumerateFiles(tree.Root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, f => Convert.ToHexString(File.ReadAllBytes(f))).Should().Equal(before);
        Encoding.UTF8.GetString(File.ReadAllBytes(tree.Full("private/people/carol.md"))).Should().Be(Carol);
    }
}
