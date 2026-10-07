using System.Text;

using Zyggy.Core.LinkedIn;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.LinkedIn;

/// <summary>The adapter's token exchange and user info (spec 36 AC-13, AC-17): the exact form, the bearer header only, no redirect.</summary>
public sealed class LinkedInApiAuthTests : IDisposable
{
    private readonly StubLinkedInHandler _stub = StubLinkedInHandler.SignInOk().WatchUris(LinkedInFixture.AccessToken, LinkedInFixture.ClientSecret);
    private readonly LinkedInHttp _http;
    private readonly LinkedInApi _api;

    public LinkedInApiAuthTests()
    {
        _http = new LinkedInHttp(_stub, null, null);
        _api = new LinkedInApi(_http, LinkedInEndpoints.Resolve(new Dictionary<string, string?>()).Routes!);
    }

    public void Dispose()
    {
        _http.Dispose();
        _stub.Violations.Should().BeEmpty();
    }

    [Fact]
    public async Task Exchange_FormBodyExact_PostToAccessToken()
    {
        // Act
        var result = await Exchange();

        // Assert
        result.Should().Be(new TokenExchangeResult(LinkedInFixture.AccessToken, 5184000, "openid,profile,w_member_social", null));
        var request = _stub.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.ToString().Should().Be("https://www.linkedin.com/oauth/v2/accessToken");
        request.Headers["Content-Type"].Should().Be("application/x-www-form-urlencoded");
        request.Body.Should().Be(
            "grant_type=authorization_code&code=c%2Fode&client_id=clientid0001&client_secret=client-secret-test-0001&redirect_uri=https%3A%2F%2Flocalhost%2Fcb");
        request.Headers.Should().NotContainKey("Authorization");
    }

    [Fact]
    public async Task Exchange_NoRedirectFollowed_Failure()
    {
        // Arrange
        _stub.Once("POST", StubLinkedInHandler.TokenUrl, 302, "{}", "Location: https://evil.example/");

        // Act
        var result = await Exchange();

        // Assert
        result.Error.Should().Be("http_302");
        _stub.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Exchange_TransportFailure_RequestFailed()
    {
        // Arrange
        _stub.OnceThrow("POST", StubLinkedInHandler.TokenUrl, "secret client-secret-test-0001 in message");

        // Act
        var result = await Exchange();

        // Assert
        result.Should().Be(new TokenExchangeResult(null, null, null, "request_failed"));
    }

    [Theory]
    [InlineData("""{"error":"invalid_request!! </>"}""", "invalid_request")]
    [InlineData("""{"message":"x"}""", "http_400")]
    [InlineData("not json", "http_400")]
    [InlineData("""{"error":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task Exchange_Error_SanitisedCode(string body, string error)
    {
        // Arrange
        _stub.Once("POST", StubLinkedInHandler.TokenUrl, 400, body);

        // Act
        var result = await Exchange();

        // Assert
        result.Error.Should().Be(error);
    }

    [Fact]
    public async Task Exchange_200WithoutExpiry_InvalidResponse()
    {
        // Arrange
        _stub.Once("POST", StubLinkedInHandler.TokenUrl, 200, """{"access_token":"x"}""");

        // Act
        var result = await Exchange();

        // Assert
        result.Error.Should().Be("invalid_response");
    }

    [Fact]
    public async Task Userinfo_BearerHeaderOnly_TokenNotInUri()
    {
        // Act
        var result = await _api.GetUserInfoAsync(LinkedInFixture.AccessToken, CancellationToken.None);

        // Assert
        result.Should().Be(new UserInfoResult("sub-alice-0001", "Alice Example", null));
        var request = _stub.Requests.Should().ContainSingle().Subject;
        request.Uri.ToString().Should().Be("https://api.linkedin.com/v2/userinfo");
        request.Headers["Authorization"].Should().Be("Bearer " + LinkedInFixture.AccessToken);
        request.Body.Should().BeNull();
    }

    [Theory]
    [InlineData(401, "{}", "http_401")]
    [InlineData(200, """{"name":"Alice"}""", "invalid_response")]
    public async Task Userinfo_Failure_Error(int status, string body, string error)
    {
        // Arrange
        _stub.Once("GET", StubLinkedInHandler.UserInfoUrl, status, body);

        // Act
        var result = await _api.GetUserInfoAsync(LinkedInFixture.AccessToken, CancellationToken.None);

        // Assert
        result.Error.Should().Be(error);
    }

    [Fact]
    public async Task StubViolations_Empty()
    {
        // Act
        await Exchange();
        await _api.GetUserInfoAsync(LinkedInFixture.AccessToken, CancellationToken.None);

        // Assert
        _stub.Violations.Should().BeEmpty();
        _stub.Requests.Should().HaveCount(2);
    }

    private Task<TokenExchangeResult> Exchange() =>
        _api.ExchangeCodeAsync("c/ode", "clientid0001", Encoding.UTF8.GetBytes(LinkedInFixture.ClientSecret), "https://localhost/cb", CancellationToken.None);
}
