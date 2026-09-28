using Zyggy.Core.Envelope;

namespace Zyggy.Core.Tests.Envelope;

public sealed class EnvelopeIdTests
{
    [Fact]
    public void TryParse_TwentySixCrockfordChars_ReturnsTrue()
    {
        // Act
        bool ok = EnvelopeId.TryParse("01J8Y3N7Q2X9Z4A5B6C7D8E9F0", out EnvelopeId? id);

        // Assert
        ok.Should().BeTrue();
        id!.Value.Should().Be("01J8Y3N7Q2X9Z4A5B6C7D8E9F0");
        id.ToString().Should().Be("01J8Y3N7Q2X9Z4A5B6C7D8E9F0");
        id.Timestamp.Offset.Should().Be(TimeSpan.Zero);
    }

    [Theory]
    [InlineData("01J8Y3N7Q2X9Z4A5B6C7D8E9F")]
    [InlineData("01J8Y3N7Q2X9Z4A5B6C7D8E9F00")]
    [InlineData("01j8y3n7q2x9z4a5b6c7d8e9f0")]
    [InlineData("01J8Y3N7Q2X9Z4A5B6C7D8E9FI")]
    [InlineData("01J8Y3N7Q2X9Z4A5B6C7D8E9FL")]
    [InlineData("01J8Y3N7Q2X9Z4A5B6C7D8E9FO")]
    [InlineData("01J8Y3N7Q2X9Z4A5B6C7D8E9FU")]
    [InlineData("81J8Y3N7Q2X9Z4A5B6C7D8E9F0")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_Invalid_ReturnsFalse(string? value)
    {
        // Act
        bool ok = EnvelopeId.TryParse(value, out EnvelopeId? id);

        // Assert
        ok.Should().BeFalse();
        id.Should().BeNull();
    }

    [Fact]
    public void New_GivenInstant_YieldsUppercaseCrockfordWithThatMillisecond()
    {
        // Arrange
        var instant = new DateTimeOffset(2026, 9, 27, 14, 5, 0, 123, TimeSpan.Zero);

        // Act
        EnvelopeId id = EnvelopeId.New(instant);

        // Assert
        id.Value.Should().MatchRegex("^[0-7][0-9A-HJKMNP-TV-Z]{25}$");
        id.Timestamp.Should().Be(instant);
    }

    [Fact]
    public void New_SameInstantTwice_Differs()
    {
        // Arrange
        var instant = new DateTimeOffset(2026, 9, 27, 14, 5, 0, TimeSpan.Zero);

        // Act
        EnvelopeId first = EnvelopeId.New(instant);
        EnvelopeId second = EnvelopeId.New(instant);

        // Assert
        first.Should().NotBe(second);
    }
}
