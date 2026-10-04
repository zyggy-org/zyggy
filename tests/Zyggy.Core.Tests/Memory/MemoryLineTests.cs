using Zyggy.Core.Memory;

namespace Zyggy.Core.Tests.Memory;

public sealed class MemoryLineTests
{
    [Fact]
    public void Parse_StatedWithScope_ReadsTagDateScopeAndFact()
    {
        // Act
        var line = MemoryLine.Parse("- [stated] 2026-09-18 (home): I live in Ghent.");

        // Assert
        line.Tag.Should().Be(MemoryTag.Stated);
        line.Date.Should().Be(new DateOnly(2026, 9, 18));
        line.Scope.Should().Be("home");
        line.Provenance.Should().BeEmpty();
        line.Fact.Should().Be("I live in Ghent.");
        line.TooLong.Should().BeFalse();
    }

    [Fact]
    public void Parse_StatedWithoutScope_HasNullScope()
    {
        // Act
        var line = MemoryLine.Parse("- [stated] 2026-09-18: Call me Alice.");

        // Assert
        line.Tag.Should().Be(MemoryTag.Stated);
        line.Scope.Should().BeNull();
        line.Fact.Should().Be("Call me Alice.");
    }

    [Fact]
    public void Parse_ObservedWithTwoProvenances_ReadsBoth()
    {
        // Act
        var line = MemoryLine.Parse("- [observed] 2026-10-03 [m365-mail 2026-10-03; remember 2026-10-04]: Acme renewed: 3 years.");

        // Assert
        line.Tag.Should().Be(MemoryTag.Observed);
        line.Date.Should().Be(new DateOnly(2026, 10, 3));
        line.Provenance.Should().Equal("m365-mail 2026-10-03", "remember 2026-10-04");
        line.Fact.Should().Be("Acme renewed: 3 years.");
    }

    [Fact]
    public void Parse_DailyForm_ReadsTimeAndSession()
    {
        // Act
        var line = MemoryLine.Parse("- [observed] 09:15 session 1a2b3c4d: Worked on the Zyggy plan.");

        // Assert
        line.Tag.Should().Be(MemoryTag.Observed);
        line.Date.Should().BeNull();
        line.Time.Should().Be("09:15");
        line.Session.Should().Be("1a2b3c4d");
        line.Fact.Should().Be("Worked on the Zyggy plan.");
    }

    [Fact]
    public void Parse_Over400Characters_IsFlaggedTooLong()
    {
        // Arrange
        var prefix = "- [stated] 2026-09-18: ";
        var exact = prefix + new string('x', 400 - prefix.Length);
        var over = exact + "x";

        // Assert
        MemoryLine.Parse(exact).TooLong.Should().BeFalse();
        MemoryLine.Parse(over).TooLong.Should().BeTrue();
    }

    [Theory]
    [InlineData("- [guessed] 2026-09-18: maybe")]
    [InlineData("plain text")]
    [InlineData("- [stated] yesterday: no date")]
    [InlineData("")]
    public void Parse_UnknownShape_IsOther(string text)
    {
        // Act
        var line = MemoryLine.Parse(text);

        // Assert
        line.Tag.Should().Be(MemoryTag.Other);
        line.Text.Should().Be(text);
    }
}
