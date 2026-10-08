using Zyggy.Core.Memory;

namespace Zyggy.Core.Tests.Memory;

public sealed class SlugAndCategoryTests
{
    [Theory]
    [InlineData("areas", true)]
    [InlineData("clients", true)]
    [InlineData("work", true)]
    [InlineData("a1-b2", true)]
    [InlineData("a", false)]
    [InlineData("1abc", false)]
    [InlineData("Areas", false)]
    [InlineData("-abc", false)]
    [InlineData("abc_d", false)]
    [InlineData("abcdefghijklmnopqrstuvwxyz012345", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void CategoryName_TryParse_FollowsSyntax(string? value, bool valid)
    {
        // Act
        var ok = CategoryName.TryParse(value, out var name);

        // Assert
        ok.Should().Be(valid);
        (name is not null).Should().Be(valid);
        if (valid)
        {
            name!.Value.Should().Be(value);
        }
    }

    [Theory]
    [InlineData("acme-corp", true)]
    [InlineData("work-redis", true)]
    [InlineData("0day", true)]
    [InlineData("a", true)]
    [InlineData("-a", false)]
    [InlineData("Acme", false)]
    [InlineData("acme.corp", false)]
    [InlineData("_index", false)]
    [InlineData("", false)]
    public void Slug_TryParse_FollowsSyntax(string value, bool valid)
    {
        // Act
        var ok = Slug.TryParse(value, out _);

        // Assert
        ok.Should().Be(valid);
    }

    [Fact]
    public void Slug_SixtyCharacters_IsValidAndSixtyOneIsNot()
    {
        // Assert
        Slug.TryParse(new string('a', 60), out _).Should().BeTrue();
        Slug.TryParse(new string('a', 61), out _).Should().BeFalse();
    }

    [Fact]
    public void Parse_Invalid_ThrowsFormatException()
    {
        // Assert
        FluentActions.Invoking(() => CategoryName.Parse("Bad")).Should().Throw<FormatException>();
        FluentActions.Invoking(() => Slug.Parse("Bad")).Should().Throw<FormatException>();
    }

    [Fact]
    public void MemorySide_HasPrivateAndBusinessOnly()
    {
        // Assert
        Enum.GetNames<MemorySide>().Should().Equal("Private", "Business");
        MemorySideWire.ToWire(MemorySide.Private).Should().Be("private");
        MemorySideWire.ToWire(MemorySide.Business).Should().Be("business");
        MemorySideWire.TryFromWire("work", out _).Should().BeFalse();
        MemorySideWire.TryFromWire("business", out var side).Should().BeTrue();
        side.Should().Be(MemorySide.Business);
    }
}
