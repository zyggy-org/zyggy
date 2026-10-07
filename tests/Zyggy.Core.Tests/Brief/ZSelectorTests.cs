using Zyggy.Core.Brief;

namespace Zyggy.Core.Tests.Brief;

/// <summary>Spec 35 AC-24: <c>Z1,Z3</c>, <c>Z1-Z5</c> and <c>all</c> — the numbers come only from the argument and the item list.</summary>
public sealed class ZSelectorTests
{
    [Theory]
    [InlineData("Z1,Z3", "1,3")]
    [InlineData("z2", "2")]
    [InlineData("Z1-Z3", "1,2,3")]
    [InlineData("Z1, Z3", "1,3")]
    [InlineData("Z1,Z3-Z4,Z1", "1,3,4")]
    [InlineData("Z7", "7")]
    public void Parse_ListRange(string text, string expected)
    {
        // Act
        var ok = ZSelector.TryParse(text, out var selection);

        // Assert
        ok.Should().BeTrue();
        selection.All.Should().BeFalse();
        string.Join(',', selection.Numbers).Should().Be(expected);
    }

    [Theory]
    [InlineData("all")]
    [InlineData("ALL")]
    public void Parse_All(string text)
    {
        // Act
        var ok = ZSelector.TryParse(text, out var selection);

        // Assert
        ok.Should().BeTrue();
        selection.All.Should().BeTrue();
    }

    [Theory]
    [InlineData("Z0")]
    [InlineData("Z3-Z1")]
    [InlineData("do Z1")]
    [InlineData("")]
    [InlineData("Z")]
    [InlineData("1")]
    [InlineData("Z1-")]
    [InlineData("Z1,,Z2")]
    [InlineData("Z1000")]
    [InlineData("Z1-Z200")]
    [InlineData("all,Z1")]
    public void Parse_Bad_Usage(string text)
    {
        // Act / Assert
        ZSelector.TryParse(text, out _).Should().BeFalse();
    }
}
