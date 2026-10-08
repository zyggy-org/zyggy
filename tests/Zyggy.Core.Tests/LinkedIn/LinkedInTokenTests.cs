using System.Text;

using Zyggy.Core.LinkedIn;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.LinkedIn;

/// <summary>The token file's record (spec 36 Files, Assumption 11): its shape, and that its text never shows the access token.</summary>
public sealed class LinkedInTokenTests
{
    private static byte[] GoldenToken => File.ReadAllBytes(Path.Combine(Golden.Directory, "linkedin", "token.json"));

    [Fact]
    public void TryParse_Golden_RoundTrips()
    {
        // Act
        var token = LinkedInToken.TryParse(GoldenToken);

        // Assert
        token.Should().NotBeNull();
        token!.AccessToken.Should().Be("AQV-test-0001");
        token.ExpiresAt.Should().Be(new DateTimeOffset(2026, 12, 6, 8, 0, 0, TimeSpan.Zero));
        token.Sub.Should().Be("sub-alice-0001");
        token.Name.Should().Be("Alice Example");
        token.HasScope("w_member_social").Should().BeTrue();
        token.HasScope("email").Should().BeFalse();
        token.ToBytes().Should().Equal(GoldenToken);
    }

    [Fact]
    public void HasScope_SpaceSeparated_Accepted()
    {
        // Arrange
        var token = LinkedInFixture.Token(LinkedInFixture.Now.AddDays(60), scope: "openid profile w_member_social");

        // Assert
        token.HasScope("w_member_social").Should().BeTrue();
    }

    [Fact]
    public void ToString_NeverContainsAccessToken()
    {
        // Arrange
        var token = LinkedInToken.TryParse(GoldenToken)!;

        // Act
        var text = token.ToString();

        // Assert
        text.Should().NotContain("AQV-test-0001").And.Contain("[withheld]").And.Contain("sub-alice-0001");
    }

    [Theory]
    [InlineData("access_token")]
    [InlineData("expires_at")]
    [InlineData("scope")]
    [InlineData("sub")]
    [InlineData("name")]
    [InlineData("obtained_at")]
    [InlineData("schema")]
    public void TryParse_MissingField_Null(string field)
    {
        // Arrange
        var json = Encoding.UTF8.GetString(GoldenToken);
        var start = json.IndexOf($"\"{field}\":", StringComparison.Ordinal);
        var end = json.IndexOf(",\"", start + 1, StringComparison.Ordinal);
        var without = end < 0 ? json[..(start - 1)] + "}" : json[..start] + json[(end + 1)..];

        // Act
        var token = LinkedInToken.TryParse(Encoding.UTF8.GetBytes(without));

        // Assert
        token.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("""{"schema":2,"access_token":"a","expires_at":"2026-12-06T08:00:00Z","scope":"","sub":"s","name":"n","obtained_at":"2026-10-07T08:00:00Z"}""")]
    public void TryParse_NotAToken_Null(string json)
    {
        // Act
        var token = LinkedInToken.TryParse(Encoding.UTF8.GetBytes(json));

        // Assert
        token.Should().BeNull();
    }
}
