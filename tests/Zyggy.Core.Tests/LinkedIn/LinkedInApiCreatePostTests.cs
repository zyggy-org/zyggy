using Zyggy.Core.LinkedIn;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.LinkedIn;

/// <summary>
/// The post request (spec 36 AC-2, AC-12, AC-13): the exact body and headers, the URN from <c>x-restli-id</c>, every status mapped to one
/// closed outcome, one request only, the token only in the <c>Authorization</c> header.
/// </summary>
public sealed class LinkedInApiCreatePostTests : IDisposable
{
    public const string GoldenText = "Agents (part 1) [draft] {v2} <b> a|b @name *bold* _it_ ~x~ back\\slash #dotnet # not\nSecond line 🚀\nThird";

    private readonly StubLinkedInHandler _stub = new StubLinkedInHandler()
        .Always("POST", StubLinkedInHandler.PostsUrl, 201, string.Empty, StubLinkedInHandler.Fixture("post-201.headers"))
        .WatchUris(LinkedInFixture.AccessToken);

    // 270 characters of short words: long enough to be cut, never one opaque token.
    private static readonly string Words = string.Concat(Enumerable.Repeat("no ", 90));

    private readonly LinkedInHttp _http;
    private readonly LinkedInApi _api;

    public LinkedInApiCreatePostTests()
    {
        _http = new LinkedInHttp(_stub, TimeSpan.FromMilliseconds(200), null);
        _api = new LinkedInApi(_http, LinkedInEndpoints.Resolve(new Dictionary<string, string?>()).Routes!, PublishHarness.Patterns);
    }

    public void Dispose()
    {
        _http.Dispose();
        _stub.Violations.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_BodyByteEqualsGolden_HeadersExact_OneRequest()
    {
        // Act
        await Create(LittleText.Escape(GoldenText));

        // Assert
        var request = _stub.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.ToString().Should().Be("https://api.linkedin.com/rest/posts");
        request.Body.Should().Be(File.ReadAllText(Path.Combine(Golden.Directory, "linkedin", "post-request.json")));
        request.Headers["Authorization"].Should().Be("Bearer " + LinkedInFixture.AccessToken);
        request.Headers["Linkedin-Version"].Should().Be("202609");
        request.Headers["X-Restli-Protocol-Version"].Should().Be("2.0.0");
        request.Headers["Content-Type"].Should().Be("application/json");
    }

    [Fact]
    public async Task Create_201_UrnFromHeader()
    {
        // Act
        var result = await Create("Hello");

        // Assert
        result.Should().Be(new CreatePostResult("urn:li:share:7000000000000000001", null, null));
    }

    [Fact]
    public async Task Create_201UgcPost_Urn()
    {
        // Arrange
        _stub.Once("POST", StubLinkedInHandler.PostsUrl, 201, string.Empty, "x-restli-id: urn:li:ugcPost:42");

        // Act
        var result = await Create("Hello");

        // Assert
        result.Urn.Should().Be("urn:li:ugcPost:42");
    }

    [Theory]
    [InlineData("")]
    [InlineData("x-restli-id: urn:li:person:42")]
    [InlineData("x-restli-id: urn:li:share:")]
    public async Task Create_201WithoutHeader_OutcomeUnknown(string headers)
    {
        // Arrange
        _stub.Once("POST", StubLinkedInHandler.PostsUrl, 201, string.Empty, headers);

        // Act
        var result = await Create("Hello");

        // Assert
        result.Urn.Should().BeNull();
        result.Failure.Should().Be(LinkedInFailure.OutcomeUnknown);
    }

    [Theory]
    [InlineData(401, null, "TokenExpired", null)]
    [InlineData(403, null, "Forbidden", null)]
    [InlineData(426, "post-426.json", "VersionRetired", null)]
    [InlineData(429, "post-429.json", "RateLimited", null)]
    [InlineData(400, "post-400.json", "Rejected", "FIELD_LENGTH_TOO_LONG: commentary is too long")]
    [InlineData(422, "post-422.json", "Rejected", "Unprocessable entity: author is not valid")]
    [InlineData(404, null, "Rejected", "LinkedIn answered 404")]
    [InlineData(500, null, "OutcomeUnknown", "LinkedIn answered 500")]
    [InlineData(503, null, "OutcomeUnknown", "LinkedIn answered 503")]
    public async Task Create_StatusMapping(int status, string? fixture, string failure, string? detail)
    {
        // Arrange
        _stub.Once("POST", StubLinkedInHandler.PostsUrl, status, fixture is null ? "{}" : StubLinkedInHandler.Fixture(fixture));

        // Act
        var result = await Create("Hello");

        // Assert
        result.Should().Be(new CreatePostResult(null, Enum.Parse<LinkedInFailure>(failure), detail));
        _stub.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Create_Timeout_OutcomeUnknown_OneRequest()
    {
        // Arrange: the context's timeout is 200 ms
        _stub.OnceDelayed("POST", StubLinkedInHandler.PostsUrl, TimeSpan.FromSeconds(5));

        // Act
        var result = await Create("Hello");

        // Assert
        result.Should().Be(new CreatePostResult(null, LinkedInFailure.OutcomeUnknown, "timeout"));
        _stub.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Create_ConnectionReset_OutcomeUnknown_OneRequest()
    {
        // Arrange
        _stub.OnceThrow("POST", StubLinkedInHandler.PostsUrl);

        // Act
        var result = await Create("Hello");

        // Assert
        result.Should().Be(new CreatePostResult(null, LinkedInFailure.OutcomeUnknown, "connection failed"));
        _stub.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Create_RejectedMessageWithSecret_Withheld_Cut200()
    {
        // Arrange
        _stub.Once("POST", StubLinkedInHandler.PostsUrl, 400, """{"message":"bad value BE71 0961 2345 6769"}""");
        _stub.Once("POST", StubLinkedInHandler.PostsUrl, 422, "{\"message\":\"" + Words + "\\u0007\\nend\"}");

        // Act
        var withheld = await Create("Hello");
        var cut = await Create("Hello");

        // Assert
        withheld.Detail.Should().Be("[withheld]");
        cut.Detail.Should().Be(Words[..200]);
    }

    [Fact]
    public async Task Create_TokenOnlyInAuthorizationHeader()
    {
        // Act
        await Create("Hello");

        // Assert
        var request = _stub.Requests.Single();
        request.Uri.ToString().Should().NotContain(LinkedInFixture.AccessToken);
        request.Body.Should().NotContain(LinkedInFixture.AccessToken);
        request.Headers.Where(h => h.Value.Contains(LinkedInFixture.AccessToken, StringComparison.Ordinal)).Select(h => h.Key).Should().Equal("Authorization");
    }

    [Fact]
    public async Task Create_CallerCancelled_ThrowsOperationCanceled()
    {
        // Arrange
        _stub.OnceDelayed("POST", StubLinkedInHandler.PostsUrl, TimeSpan.FromMilliseconds(150));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        // Act
        var act = () => _api.CreatePostAsync(LinkedInFixture.AccessToken, "urn:li:person:sub-alice-0001", "Hello", PostVisibility.Public, "202609", cancel.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private Task<CreatePostResult> Create(string commentary) =>
        _api.CreatePostAsync(LinkedInFixture.AccessToken, "urn:li:person:sub-alice-0001", commentary, PostVisibility.Public, "202609", CancellationToken.None);
}
