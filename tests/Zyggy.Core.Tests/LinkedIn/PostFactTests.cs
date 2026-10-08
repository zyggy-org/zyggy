using Zyggy.Core.LinkedIn;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.LinkedIn;

/// <summary>The one <c>[observed]</c> fact of a published post (spec 36 AC-22, Assumption 4).</summary>
public sealed class PostFactTests
{
    private const string Urn = "urn:li:share:7000000000000000001";
    private static readonly DateOnly Day = new(2026, 10, 7);

    [Fact]
    public void Line_FirstSentenceUrlsToLink_Cut120()
    {
        // Arrange
        var longSentence = "Agents " + string.Concat(Enumerable.Repeat("work hard ", 20)) + "every day.";

        // Act
        var first = PostFact.Line(Day, Urn, PostVisibility.Public, "Shipped the \"LinkedIn\" tool, see https://digiverse.example/blog today. More soon.");
        var cut = PostFact.Excerpt(longSentence);

        // Assert
        first.Should().Be($"- [observed] 2026-10-07 (linkedin {Urn}): Posted on LinkedIn (PUBLIC): \"Shipped the 'LinkedIn' tool, see [link] today.\"");
        cut.EnumerateRunes().Count().Should().Be(120);
        cut.Should().EndWith("…").And.StartWith("Agents work hard");
    }

    [Theory]
    [InlineData("Is this the end? Yes.", "Is this the end?")]
    [InlineData("Wow! Great.", "Wow!")]
    [InlineData("Version 3.0.1 shipped. Next.", "Version 3.0.1 shipped.")]
    [InlineData("No end mark at all", "No end mark at all")]
    [InlineData("Visit www.example.org/x now. Bye", "Visit [link] now.")]
    public void Excerpt_SentenceRules(string text, string excerpt)
    {
        // Assert
        PostFact.Excerpt(text).Should().Be(excerpt);
    }

    [Fact]
    public void Line_NewlineEndsExcerpt()
    {
        // Act
        var line = PostFact.Line(Day, Urn, PostVisibility.Connections, "First line without a stop\nSecond line. More.");

        // Assert
        line.Should().EndWith("Posted on LinkedIn (CONNECTIONS): \"First line without a stop\"");
    }

    [Fact]
    public void Write_NewFile_ByteEqualsGolden()
    {
        // Arrange
        using var tree = new MemoryTree();

        // Act
        PostFact.Write(tree.Paths, Day, PostFact.Line(Day, Urn, PostVisibility.Public, "Shipped the \"LinkedIn\" tool, see https://digiverse.example/blog today. More soon."));

        // Assert
        File.ReadAllBytes(tree.Full("inbox/linkedin-2026-10-07.md"))
            .Should().Equal(File.ReadAllBytes(Path.Combine(Golden.Directory, "linkedin", "inbox-linkedin.md")));
    }

    [Fact]
    public void Write_ExistingFile_UpdatedRewrittenLineAppended()
    {
        // Arrange
        using var tree = new MemoryTree(("inbox/linkedin-2026-10-07.md", "---\nname: linkedin 2026-10-07\ndescription: d\nupdated: 2026-10-01\n---\n- [observed] 2026-10-07 (linkedin urn:li:share:1): Posted on LinkedIn (PUBLIC): \"One.\"\n"));

        // Act
        PostFact.Write(tree.Paths, Day, PostFact.Line(Day, Urn, PostVisibility.Public, "Two."));

        // Assert
        File.ReadAllText(tree.Full("inbox/linkedin-2026-10-07.md")).Should().Be(
            "---\nname: linkedin 2026-10-07\ndescription: d\nupdated: 2026-10-07\n---\n"
            + "- [observed] 2026-10-07 (linkedin urn:li:share:1): Posted on LinkedIn (PUBLIC): \"One.\"\n"
            + $"- [observed] 2026-10-07 (linkedin {Urn}): Posted on LinkedIn (PUBLIC): \"Two.\"\n");
    }
}
