using Zyggy.Core.Dream;

namespace Zyggy.Core.Tests.Dream;

public sealed class LineHashTests
{
    // Computed by hand: printf '%s' '<line>' | sha256sum | cut -c1-16
    [Theory]
    [InlineData("- [stated] 2026-09-30: I like green tea.", "c94a98d6f7fbbcde")]
    [InlineData("- [observed] 2026-10-03 [m365-mail 2026-10-03]: Acme Corp renewed the contract.", "5d849ce77790f99b")]
    public void Of_KnownLine_EqualsHandComputedPrefix(string line, string expected)
    {
        // Act
        var hash = LineHash.Of(line);

        // Assert
        hash.Should().Be(expected);
    }

    [Fact]
    public void Of_TrailingSpaces_Ignored()
    {
        // Assert
        LineHash.Of("- [stated] 2026-09-30: I like green tea.  \t\r").Should().Be("c94a98d6f7fbbcde");
    }
}
