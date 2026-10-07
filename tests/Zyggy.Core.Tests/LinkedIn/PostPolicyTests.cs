using Zyggy.Core.LinkedIn;

namespace Zyggy.Core.Tests.LinkedIn;

/// <summary>The local text checks (spec 36 AC-4, OQ-5): the first reason in order; URLs and hashtags allowed; nothing matched echoed.</summary>
public sealed class PostPolicyTests
{
    [Theory]
    [InlineData("", "empty")]
    [InlineData("  \n\t ", "empty")]
    [InlineData("tab\there", "control character")]
    [InlineData("line one\r\nline two", "control character")]
    [InlineData("bell\u0007", "control character")]
    [InlineData("Pay to BE71 0961 2345 6769 today", "secret pattern iban")]
    [InlineData("my api_key = abcdef123456", "secret pattern credential-assignment")]
    [InlineData("Write to someone@domain.example soon", "e-mail address")]
    [InlineData("Call +32 470 12 34 56 now", "phone number")]
    [InlineData("Call 0470 12 34 56 now", "phone number")]
    public void Check_Reasons(string text, string reason)
    {
        // Act
        var result = PostPolicy.Check(text, 3000, PublishHarness.Patterns);

        // Assert
        result.Should().Be(reason);
    }

    [Fact]
    public void Check_TooLong_NamesCountAndMax()
    {
        // Act
        var result = PostPolicy.Check(new string('x', 3001), 3000, PublishHarness.Patterns);

        // Assert
        result.Should().Be("too long (3001 > 3000)");
    }

    [Fact]
    public void Check_LoweredMax()
    {
        // Act
        var result = PostPolicy.Check(new string('x', 2901), 2900, PublishHarness.Patterns);

        // Assert
        result.Should().Be("too long (2901 > 2900)");
    }

    [Theory]
    [InlineData("Read the write-up: https://digiverse.example/blog/agents?ref=li #AI #dotnet")]
    [InlineData("Line one\nLine two (with brackets) and *stars* — ✨ #100DaysOfCode")]
    [InlineData("Version 3.0.1 shipped on 2026-10-07.")]
    public void Check_UrlAndHashtag_Allowed(string text)
    {
        // Act
        var result = PostPolicy.Check(text, 3000, PublishHarness.Patterns);

        // Assert
        result.Should().BeNull();
    }

    [Theory]
    [InlineData("Pay to BE71 0961 2345 6769 today", "BE71")]
    [InlineData("Write to someone@domain.example soon", "someone")]
    [InlineData("Call +32 470 12 34 56 now", "470")]
    public void Check_ReasonNeverContainsMatchedValue(string text, string fragment)
    {
        // Act
        var result = PostPolicy.Check(text, 3000, PublishHarness.Patterns);

        // Assert
        result.Should().NotBeNull().And.NotContain(fragment);
    }
}
