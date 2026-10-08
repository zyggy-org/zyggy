using Zyggy.Core.LinkedIn;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.LinkedIn;

/// <summary>
/// The image requests (plan 36b L1, L2, L5, D5, D10): <c>initializeUpload</c> with the member as owner, the PUT of exactly the bytes to an
/// upload address on LinkedIn's upload prefix only, and the post body with <c>content.media</c>; every failure one reason, no token anywhere
/// but the <c>Authorization</c> header.
/// </summary>
public sealed class LinkedInApiImageTests : IDisposable
{
    private const string Owner = "urn:li:person:sub-alice-0001";
    private const string ImageUrn = "urn:li:image:C4E10AQFoyyAjHPMQuQ";
    private const string Upload = "https://www.linkedin.com/dms-uploads/C4E10AQFoyyAjHPMQuQ/uploaded-image/0?ca=vector_feedshare&cn=uploads&sync=0&v=beta&ut=08zH";

    private static readonly string InitializeOk =
        $$$"""{"value":{"uploadUrlExpiresAt":1650567510704,"uploadUrl":"{{{Upload}}}","image":"{{{ImageUrn}}}"}}""";

    private readonly StubLinkedInHandler _stub = new StubLinkedInHandler()
        .Always("POST", StubLinkedInHandler.ImagesUrl, 200, InitializeOk)
        .Always("PUT", StubLinkedInHandler.UploadUrl, 201, string.Empty)
        .Always("POST", StubLinkedInHandler.PostsUrl, 201, string.Empty, StubLinkedInHandler.Fixture("post-201.headers"))
        .WatchUris(LinkedInFixture.AccessToken);

    private readonly LinkedInHttp _http;
    private readonly LinkedInApi _api;

    public LinkedInApiImageTests()
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
    public async Task Initialize_200_ReturnsUploadUrlAndUrn_ExactRequest()
    {
        // Act
        var result = await Initialize();

        // Assert
        result.Should().Be(new ImageUploadStart(new ImageUploadTicket(Upload, ImageUrn), null));
        var request = _stub.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.ToString().Should().Be("https://api.linkedin.com/rest/images?action=initializeUpload");
        request.Body.Should().Be("""{"initializeUploadRequest":{"owner":"urn:li:person:sub-alice-0001"}}""");
        request.Headers["Authorization"].Should().Be("Bearer " + LinkedInFixture.AccessToken);
        request.Headers["Linkedin-Version"].Should().Be("202609");
        request.Headers["X-Restli-Protocol-Version"].Should().Be("2.0.0");
        request.Headers["Content-Type"].Should().Be("application/json");
    }

    [Theory]
    [InlineData(401, "{}", "initialize: LinkedIn answered 401")]
    [InlineData(403, """{"message":"Accessing this image resource is forbidden"}""", "initialize: LinkedIn answered 403")]
    [InlineData(500, "{}", "initialize: LinkedIn answered 500")]
    [InlineData(200, "not json", "initialize: no upload address")]
    [InlineData(200, """{"value":{"uploadUrl":"https://www.linkedin.com/dms-uploads/x","image":"urn:li:share:1"}}""", "initialize: no upload address")]
    [InlineData(200, """{"value":{"uploadUrl":"https://evil.example/dms-uploads/x","image":"urn:li:image:A1"}}""", "upload address not allowed")]
    [InlineData(200, """{"value":{"uploadUrl":"https://www.linkedin.com/feed/x","image":"urn:li:image:A1"}}""", "upload address not allowed")]
    [InlineData(200, """{"value":{"uploadUrl":"http://www.linkedin.com/dms-uploads/x","image":"urn:li:image:A1"}}""", "upload address not allowed")]
    public async Task Initialize_Failures_OneReason(int status, string body, string reason)
    {
        // Arrange
        _stub.Once("POST", StubLinkedInHandler.ImagesUrl, status, body);

        // Act
        var result = await Initialize();

        // Assert
        result.Should().Be(new ImageUploadStart(null, reason));
    }

    [Fact]
    public async Task Initialize_Timeout_Reason()
    {
        // Arrange
        _stub.OnceDelayed("POST", StubLinkedInHandler.ImagesUrl, TimeSpan.FromSeconds(2), 200, InitializeOk);

        // Act + Assert
        (await Initialize()).Should().Be(new ImageUploadStart(null, "initialize: timeout"));
    }

    [Fact]
    public async Task Upload_Put_ExactBytesAndHeaders_Ok()
    {
        // Arrange
        var bytes = ImageBytes.Png(1200, 1200);

        // Act
        var reason = await _api.UploadImageAsync(LinkedInFixture.AccessToken, Upload, bytes, "image/png", CancellationToken.None);

        // Assert
        reason.Should().BeNull();
        var request = _stub.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Put);
        request.Uri.ToString().Should().Be(Upload);
        request.Bytes.Should().Equal(bytes);
        request.Headers["Authorization"].Should().Be("Bearer " + LinkedInFixture.AccessToken);
        request.Headers["Content-Type"].Should().Be("image/png");
    }

    [Theory]
    [InlineData(400, "upload: LinkedIn answered 400")]
    [InlineData(500, "upload: LinkedIn answered 500")]
    public async Task Upload_Failures_OneReason(int status, string reason)
    {
        // Arrange
        _stub.Once("PUT", StubLinkedInHandler.UploadUrl, status, "{}");

        // Act + Assert
        (await _api.UploadImageAsync(LinkedInFixture.AccessToken, Upload, ImageBytes.Png(1, 1), "image/png", CancellationToken.None)).Should().Be(reason);
    }

    [Fact]
    public async Task Upload_200_Ok_And_Timeout_Reason()
    {
        // Arrange
        _stub.Once("PUT", StubLinkedInHandler.UploadUrl, 200, string.Empty);
        _stub.OnceDelayed("PUT", StubLinkedInHandler.UploadUrl, TimeSpan.FromSeconds(2), 201, string.Empty);

        // Act + Assert
        (await _api.UploadImageAsync(LinkedInFixture.AccessToken, Upload, ImageBytes.Png(1, 1), "image/png", CancellationToken.None)).Should().BeNull();
        (await _api.UploadImageAsync(LinkedInFixture.AccessToken, Upload, ImageBytes.Png(1, 1), "image/png", CancellationToken.None))
            .Should().Be("upload: timeout");
    }

    [Fact]
    public async Task Upload_AddressNotAllowed_NoRequest()
    {
        // Act
        var reason = await _api.UploadImageAsync(LinkedInFixture.AccessToken, "https://evil.example/dms-uploads/x", ImageBytes.Png(1, 1), "image/png", CancellationToken.None);

        // Assert
        reason.Should().Be("upload address not allowed");
        _stub.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task CreatePost_WithMedia_BodyEqualsGolden()
    {
        // Act
        var result = await _api.CreatePostAsync(
            LinkedInFixture.AccessToken, Owner, "Hello", PostVisibility.Public, "202609", CancellationToken.None, new PostMedia(ImageUrn, "A thumb on Deny"));

        // Assert
        result.Urn.Should().NotBeNull();
        _stub.Requests.Should().ContainSingle().Subject.Body
            .Should().Be(File.ReadAllText(Path.Combine(Golden.Directory, "linkedin", "post-request-image.json")));
    }

    [Fact]
    public void PostBody_WithMediaNoAlt_OmitsAltText()
    {
        // Act
        var body = System.Text.Encoding.UTF8.GetString(LinkedInApi.PostBody(Owner, "Hello", PostVisibility.Connections, new PostMedia(ImageUrn, null)));

        // Assert
        body.Should().Contain("""
            "content":{"media":{"id":"urn:li:image:C4E10AQFoyyAjHPMQuQ"}},"lifecycleState"
            """);
    }

    private Task<ImageUploadStart> Initialize() =>
        _api.InitializeImageUploadAsync(LinkedInFixture.AccessToken, Owner, "202609", CancellationToken.None);
}
