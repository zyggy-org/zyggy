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

    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("""{"text":"Hi","visibility":"PUBLIC","image_path":"/m/a.png","image_sha256":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"}""", true)]
    [InlineData("""{"text":"Hi","visibility":"PUBLIC","image_path":"/m/a.png","image_sha256":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef","image_alt":"A thumb on Deny"}""", true)]
    [InlineData("""{"text":"Hi","visibility":"PUBLIC","image_path":"/m/a.png"}""", false)]
    [InlineData("""{"text":"Hi","visibility":"PUBLIC","image_sha256":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"}""", false)]
    [InlineData("""{"text":"Hi","visibility":"PUBLIC","image_alt":"alone"}""", false)]
    [InlineData("""{"text":"Hi","visibility":"PUBLIC","image_path":"/m/a.png","image_sha256":"0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF"}""", false)]
    [InlineData("""{"text":"Hi","visibility":"PUBLIC","image_path":"/m/a.png","image_sha256":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde"}""", false)]
    [InlineData("""{"text":"Hi","visibility":"PUBLIC","image_path":"","image_sha256":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"}""", false)]
    [InlineData("""{"text":"Hi","visibility":"PUBLIC","image_path":"/m/a.png","image_sha256":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef","image_alt":"tab\there"}""", false)]
    [InlineData("""{"text":"Hi","visibility":"PUBLIC","image_path":5,"image_sha256":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"}""", false)]
    [InlineData("""{"text":"Hi","visibility":"PUBLIC","image_path":"/m/a.png","image_sha256":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef","video":"x"}""", false)]
    public void TryParse_ImageKeys_PairingAndShapes(string json, bool valid)
    {
        // Act
        var ok = PostArguments.TryParse(JsonDocument.Parse(json).RootElement, out var parsed, out var reason);

        // Assert
        ok.Should().Be(valid);
        if (valid)
        {
            parsed!.Image.Should().NotBeNull();
            parsed.Image!.Path.Should().Be("/m/a.png");
            parsed.Image.Sha256.Should().Be(Hash);
        }
        else
        {
            reason.Should().Be("invalid arguments");
        }
    }

    [Fact]
    public void TryParse_AltTooLong_Invalid_At300Ok()
    {
        // Arrange
        static string Json(int n) => JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["text"] = "Hi",
            ["visibility"] = "PUBLIC",
            ["image_path"] = "/m/a.png",
            ["image_sha256"] = Hash,
            ["image_alt"] = new string('a', n),
        });

        // Act + Assert
        PostArguments.TryParse(JsonDocument.Parse(Json(300)).RootElement, out var ok, out _).Should().BeTrue();
        ok!.Image!.Alt.Should().HaveLength(300);
        PostArguments.TryParse(JsonDocument.Parse(Json(301)).RootElement, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_TextOnly_ImageNull()
    {
        // Act
        PostArguments.TryParse(PublishHarness.Arguments("Hi"), out var parsed, out _).Should().BeTrue();

        // Assert
        parsed!.Image.Should().BeNull();
    }
}
