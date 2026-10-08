using System.Text.Json;

using Zyggy.Core.LinkedIn;

namespace Zyggy.Core.Tests.LinkedIn;

/// <summary>The tool's arguments (spec 36 AC-1): exactly <c>text</c> and <c>visibility</c>, the text kept byte for byte.</summary>
public sealed class PostArgumentsTests
{
    [Theory]
    [InlineData("""{"text":"Hello","visibility":"PUBLIC","extra":1}""")]
    [InlineData("""{"text":"Hello"}""")]
    [InlineData("""{"visibility":"PUBLIC"}""")]
    [InlineData("""{"text":5,"visibility":"PUBLIC"}""")]
    [InlineData("""{"text":"Hello","visibility":"public"}""")]
    [InlineData("""{"text":"Hello","visibility":"LOGGED_IN"}""")]
    [InlineData("""{"text":"Hello","visibility":null}""")]
    [InlineData("""{"text":"Hello","Visibility":"PUBLIC"}""")]
    [InlineData("""["Hello","PUBLIC"]""")]
    [InlineData("null")]
    public void TryParse_ExtraMissingWrongTypeVisibilityUnknown_Invalid(string json)
    {
        // Act
        var ok = PostArguments.TryParse(JsonDocument.Parse(json).RootElement, out var parsed, out var reason);

        // Assert
        ok.Should().BeFalse();
        parsed.Should().BeNull();
        reason.Should().Be("invalid arguments");
    }

    [Fact]
    public void TryParse_TextKeptByteForByte_LeadingTrailingSpacesAndNewlines()
    {
        // Arrange
        const string Text = "  Two lines\n\nwith trailing spaces  \n";

        // Act
        var ok = PostArguments.TryParse(PublishHarness.Arguments(Text, "CONNECTIONS"), out var parsed, out _);

        // Assert
        ok.Should().BeTrue();
        parsed.Should().Be(new PostArguments(Text, PostVisibility.Connections));
    }

    [Fact]
    public void TryParse_MaxCharsCountsScalarValues_EmojiIsOne()
    {
        // Act
        var ok = PostArguments.TryParse(PublishHarness.Arguments("ok 🚀"), out var parsed, out _);

        // Assert
        ok.Should().BeTrue();
        parsed!.Text.Length.Should().Be(5);
        parsed.Characters.Should().Be(4);
        PostPolicy.Check(new string('a', 2999) + "🚀", 3000, Zyggy.Core.Memory.SecretPatterns.None).Should().BeNull();
        PostPolicy.Check(new string('a', 3000) + "🚀", 3000, Zyggy.Core.Memory.SecretPatterns.None).Should().Be("too long (3001 > 3000)");
    }
}
