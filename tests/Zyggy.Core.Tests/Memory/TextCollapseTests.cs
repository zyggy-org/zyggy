using Zyggy.Core.Memory;

namespace Zyggy.Core.Tests.Memory;

/// <summary><c>zy_collapse_line</c> and <c>zy_char_count</c> of the template's <c>lib.sh</c>, in .NET.</summary>
public sealed class TextCollapseTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a\rb", "a b")]
    [InlineData("a\nb", "a b")]
    [InlineData("a\tb", "a b")]
    [InlineData("a\r\n\tb", "a b")]
    [InlineData("a  b   c", "a b c")]
    [InlineData(" a ", "a")]
    [InlineData("   a   ", "a")]
    [InlineData("  Marie\r\nlikes\t\tgreen   tea \n", "Marie likes green tea")]
    [InlineData(" \t\r\n ", "")]
    [InlineData("", "")]
    [InlineData("a  b", "a  b")]
    [InlineData("a\vb", "a\vb")]
    public void Line_Input_CollapsedLikeTrTrSed(string input, string expected)
    {
        // Act
        var line = TextCollapse.Line(input);

        // Assert
        line.Should().Be(expected);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("abc", 3)]
    [InlineData("é", 1)]
    [InlineData("日本語", 3)]
    [InlineData("😀x", 2)]
    public void CharCount_Input_CountsCharactersLikeWcM(string input, int expected)
    {
        // Act
        var count = TextCollapse.CharCount(input);

        // Assert
        count.Should().Be(expected);
    }
}
