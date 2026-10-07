using Zyggy.Core.LinkedIn;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.LinkedIn;

/// <summary>The LinkedIn HTTP exchange (spec 36 AC-13): HTTPS or the loopback stand-in only, no redirect followed, the context's timeout.</summary>
public sealed class LinkedInHttpTests : IDisposable
{
    private readonly StubLinkedInHandler _stub = new();

    public void Dispose() => _stub.Violations.Should().BeEmpty();

    [Theory]
    [InlineData("http://api.linkedin.com/rest/posts")]
    [InlineData("http://127.0.0.1:5000/rest/posts")]
    public async Task Send_HttpNonLoopback_Throws(string url)
    {
        // Arrange
        using var http = new LinkedInHttp(_stub, null, loopback: null);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        // Act
        var act = () => http.SendAsync(request, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        _stub.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Send_OtherLoopbackPort_Throws()
    {
        // Arrange
        using var http = new LinkedInHttp(_stub, null, new Uri("http://127.0.0.1:5000"));
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:5001/v2/userinfo");

        // Act
        var act = () => http.SendAsync(request, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Send_RedirectNotFollowed()
    {
        // Arrange
        _stub.Always("GET", StubLinkedInHandler.UserInfoUrl, 302, "{}", "Location: https://evil.example/");
        using var http = new LinkedInHttp(_stub, null, null);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.linkedin.com/v2/userinfo");

        // Act
        using var response = await http.SendAsync(request, CancellationToken.None);

        // Assert
        ((int)response.StatusCode).Should().Be(302);
        _stub.Requests.Should().ContainSingle();
    }

    [Fact]
    public void Timeout_IsTheContextValue()
    {
        // Act
        using var given = new LinkedInHttp(_stub, TimeSpan.FromMilliseconds(200), null);
        using var standard = new LinkedInHttp(null, null, null);

        // Assert
        given.Timeout.Should().Be(TimeSpan.FromMilliseconds(200));
        standard.Timeout.Should().Be(TimeSpan.FromSeconds(30));
    }
}
