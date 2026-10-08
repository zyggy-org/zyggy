using Zyggy.Core.LinkedIn;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.LinkedIn;

/// <summary>LinkedIn's addresses and the test-only loopback override (spec 36 AC-15).</summary>
public sealed class LinkedInEndpointsTests
{
    [Fact]
    public void Resolve_NoOverride_LinkedInHosts()
    {
        // Act
        var routes = LinkedInEndpoints.Resolve(new Dictionary<string, string?>()).Routes!;

        // Assert
        routes.AccessTokenUrl.Should().Be("https://www.linkedin.com/oauth/v2/accessToken");
        routes.UserInfoUrl.Should().Be("https://api.linkedin.com/v2/userinfo");
        routes.PostsUrl.Should().Be("https://api.linkedin.com/rest/posts");
        routes.Loopback.Should().BeNull();
    }

    [Theory]
    [InlineData("http://127.0.0.1:5000", true)]
    [InlineData("http://127.0.0.1:1", true)]
    [InlineData("http://127.0.0.1:65535", true)]
    [InlineData("http://127.0.0.1:65536", false)]
    [InlineData("http://127.0.0.1:0", false)]
    [InlineData("http://localhost:5000", false)]
    [InlineData("https://127.0.0.1:5000", false)]
    [InlineData("http://127.0.0.1:5000/x", false)]
    [InlineData("http://127.0.0.1:5000/", false)]
    [InlineData("http://10.0.0.1:5000", false)]
    [InlineData("http://127.0.0.1", false)]
    [InlineData("http://127.0.0.1:5000 ", false)]
    public void Resolve_Override(string value, bool accepted)
    {
        // Act
        var load = LinkedInEndpoints.Resolve(new Dictionary<string, string?> { ["ZYGGY_LINKEDIN_API_BASE"] = value });

        // Assert
        if (accepted)
        {
            load.Error.Should().BeNull();
            load.Routes!.PostsUrl.Should().Be(value + "/rest/posts");
            load.Routes.AccessTokenUrl.Should().Be(value + "/oauth/v2/accessToken");
            load.Routes.Loopback.Should().Be(new Uri(value));
        }
        else
        {
            load.Routes.Should().BeNull();
            load.Error.Should().Be("configuration error: ZYGGY_LINKEDIN_API_BASE must be a loopback address");
        }
    }

    [Fact]
    public async Task AuthFinish_NonLoopbackOverride_ExitThree()
    {
        // Arrange
        using var fixture = new LinkedInFixture();
        fixture.Environment["ZYGGY_LINKEDIN_API_BASE"] = "http://10.0.0.1:5000";
        var console = new VerbConsole("https://localhost/zyggy/linkedin?code=c&state=s\n");

        // Act
        var exit = await fixture.Host().RunAsync(["auth", "finish"], console.Io, CancellationToken.None);

        // Assert
        exit.Should().Be(3);
        console.Stderr.Should().Be("linkedin: configuration error: ZYGGY_LINKEDIN_API_BASE must be a loopback address\n");
    }

    [Fact]
    public void FeedUpdateUrl_NeverOverridden()
    {
        // Assert
        LinkedInEndpoints.FeedUpdateUrl("urn:li:share:7000000000000000001")
            .Should().Be("https://www.linkedin.com/feed/update/urn:li:share:7000000000000000001/");
    }
}
