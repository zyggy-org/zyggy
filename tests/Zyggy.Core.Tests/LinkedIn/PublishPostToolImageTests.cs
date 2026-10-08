using System.Text.Json;

using Zyggy.Core.LinkedIn;

namespace Zyggy.Core.Tests.LinkedIn;

/// <summary>
/// <c>publish_post</c> with an image (plan 36b D3–D7): every local check before any request, then initialize → upload → one settle wait
/// → one post carrying the image; upload failures publish nothing and are <c>rejected: image upload: …</c>; the duplicate key covers the
/// image; the row names the image's hash, size and URN; the fact says "(with an image)".
/// </summary>
public sealed class PublishPostToolImageTests : IDisposable
{
    private const string Text = "Human in the loop. Best feature I ever built.";
    private const string ImageUrn = "urn:li:image:C4E10AQFoyyAjHPMQuQ";
    private const string Urn = "urn:li:share:7000000000000000001";

    private readonly RecordingApi _api = new();
    private readonly string _media = Path.Combine(Path.GetTempPath(), "zyggy-ut", Guid.NewGuid().ToString("N"), "media");
    private readonly PublishHarness _harness;
    private readonly byte[] _png = ImageBytes.Png(1200, 1200);
    private readonly string _pngPath;

    public PublishPostToolImageTests()
    {
        Directory.CreateDirectory(_media);
        _pngPath = Path.Combine(_media, "deny.png");
        File.WriteAllBytes(_pngPath, _png);
        _harness = new PublishHarness(_api, Instance(settleMs: 0));
    }

    public void Dispose()
    {
        _harness.Dispose();
        Directory.Delete(Path.GetDirectoryName(_media)!, recursive: true);
    }

    [Fact]
    public async Task Call_WithImage_InitializeUploadPost_ResultPublished_RowAndFact()
    {
        // Act
        var result = await Call(_pngPath, ImageBytes.Sha256(_png), "A thumb on Deny");

        // Assert
        result.Should().Be(new ToolCallResult(false, $"published: {Urn} — https://www.linkedin.com/feed/update/{Urn}/"));
        _api.Calls.Should().Equal("initialize urn:li:person:sub-alice-0001", "upload image/png " + _png.Length, "post " + ImageUrn + " A thumb on Deny");
        _api.Uploaded.Should().Equal(_png);
        var row = _harness.Rows().Should().ContainSingle().Subject;
        row.GetProperty("status").GetString().Should().Be("ok");
        row.GetProperty("sha256").GetString().Should().Be(PublishPostTool.Sha256(Text + "\n" + ImageBytes.Sha256(_png)));
        row.GetProperty("image_sha256").GetString().Should().Be(ImageBytes.Sha256(_png));
        row.GetProperty("image_bytes").GetInt32().Should().Be(_png.Length);
        row.GetProperty("image_urn").GetString().Should().Be(ImageUrn);
        File.ReadAllText(_harness.Memory.Full("inbox/linkedin-2026-10-07.md"))
            .Should().EndWith($"- [observed] 2026-10-07 (linkedin {Urn}): Posted on LinkedIn (PUBLIC): \"Human in the loop.\" (with an image)\n");
    }

    [Fact]
    public async Task Call_WithImage_WaitsSettleOnceBetweenUploadAndPost()
    {
        // Arrange
        _harness.Configuration = PublishHarness.Load(Instance(settleMs: 3000));

        // Act
        var call = Call(_pngPath, ImageBytes.Sha256(_png), null);
        _api.Calls.Should().Equal("initialize urn:li:person:sub-alice-0001", "upload image/png " + _png.Length);
        _harness.Clock.Advance(TimeSpan.FromMilliseconds(2999));
        _api.Calls.Should().HaveCount(2);
        _harness.Clock.Advance(TimeSpan.FromMilliseconds(1));
        var result = await call;

        // Assert
        result.IsError.Should().BeFalse(result.Text);
        _api.Calls.Last().Should().Be("post " + ImageUrn + " -");
    }

    [Theory]
    [InlineData("outside", "refused: image outside image.dir")]
    [InlineData("missing", "refused: image not a regular file")]
    [InlineData("text", "refused: image not PNG, JPEG or GIF")]
    [InlineData("hash", "refused: image hash mismatch")]
    public async Task Call_ImageRefusals_NoRequest_OneRow(string kind, string expected)
    {
        // Arrange
        var text = Path.Combine(_media, "notes.png");
        File.WriteAllText(text, "not an image");
        var (path, sha) = kind switch
        {
            "outside" => (Path.Combine(Path.GetTempPath(), "elsewhere.png"), ImageBytes.Sha256(_png)),
            "missing" => (Path.Combine(_media, "missing.png"), ImageBytes.Sha256(_png)),
            "text" => (text, ImageBytes.Sha256(File.ReadAllBytes(text))),
            _ => (_pngPath, new string('0', 64)),
        };

        // Act
        var result = await Call(path, sha, null);

        // Assert
        result.Should().Be(new ToolCallResult(true, expected));
        _api.Calls.Should().BeEmpty();
        _harness.Rows().Should().ContainSingle().Which.GetProperty("status").GetString().Should().Be(expected);
    }

    [Fact]
    public async Task Call_ImageTooLarge_RefusedBeforeToken()
    {
        // Arrange
        _harness.Configuration = PublishHarness.Load(Instance(settleMs: 0, maxBytes: 10));
        _harness.SetToken(null);

        // Act
        var result = await Call(_pngPath, ImageBytes.Sha256(_png), null);

        // Assert
        result.Text.Should().Be($"refused: image too large ({_png.Length} > 10)");
        _api.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Call_ImageWithoutToken_NotConnected_NoRequest()
    {
        // Arrange
        _harness.SetToken(null);

        // Act
        var result = await Call(_pngPath, ImageBytes.Sha256(_png), null);

        // Assert
        result.Text.Should().StartWith("not_connected:");
        _api.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("initialize", "rejected: image upload: initialize: LinkedIn answered 403")]
    [InlineData("upload", "rejected: image upload: upload: LinkedIn answered 500")]
    public async Task Call_UploadFailures_Rejected_NoPostSent(string failAt, string expected)
    {
        // Arrange
        _api.FailInitialize = failAt == "initialize" ? "initialize: LinkedIn answered 403" : null;
        _api.FailUpload = failAt == "upload" ? "upload: LinkedIn answered 500" : null;

        // Act
        var result = await Call(_pngPath, ImageBytes.Sha256(_png), null);

        // Assert
        result.Should().Be(new ToolCallResult(true, expected));
        _api.Calls.Should().NotContain(c => c.StartsWith("post", StringComparison.Ordinal));
        _harness.Rows().Single().GetProperty("status").GetString().Should().Be(expected);
        _harness.MemoryFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task Call_PostRejectedAfterUpload_Rejected_NoRetry()
    {
        // Arrange
        _api.PostResult = new CreatePostResult(null, LinkedInFailure.Rejected, "image not ready");

        // Act
        var result = await Call(_pngPath, ImageBytes.Sha256(_png), null);

        // Assert
        result.Should().Be(new ToolCallResult(true, "rejected: image not ready"));
        _api.Calls.Count(c => c.StartsWith("post", StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact]
    public async Task Call_SameTextSameImage_Duplicate_SameTextOtherImageOrNoImage_NotDuplicate()
    {
        // Arrange
        var other = ImageBytes.Png(800, 800);
        var otherPath = Path.Combine(_media, "other.png");
        File.WriteAllBytes(otherPath, other);
        (await Call(_pngPath, ImageBytes.Sha256(_png), null)).IsError.Should().BeFalse();

        // Act
        var same = await Call(_pngPath, ImageBytes.Sha256(_png), null);
        var otherImage = await Call(otherPath, ImageBytes.Sha256(other), null);
        var textOnly = await _harness.CallAsync(Text);

        // Assert
        same.Text.Should().StartWith($"refused: duplicate of {Urn} posted 2026-10-07 10:00");
        otherImage.IsError.Should().BeFalse(otherImage.Text);
        textOnly.IsError.Should().BeFalse(textOnly.Text);
    }

    [Fact]
    public async Task Call_TextOnly_RowHasNoImageKeys()
    {
        // Act
        await _harness.CallAsync(Text);

        // Assert
        var row = _harness.Rows().Single();
        row.TryGetProperty("image_sha256", out _).Should().BeFalse();
        row.GetProperty("sha256").GetString().Should().Be(PublishPostTool.Sha256(Text));
        _api.Calls.Should().Equal("post - -");
    }

    private string Instance(int settleMs, int maxBytes = 10485760) =>
        "{\"client_id\":\"clientid0001\",\"redirect_uri\":\"https://localhost/zyggy/linkedin\",\"image\":{\"dir\":"
        + JsonSerializer.Serialize(_media) + ",\"max_bytes\":" + maxBytes + ",\"settle_ms\":" + settleMs + "}}";

    private Task<ToolCallResult> Call(string path, string sha, string? alt)
    {
        var arguments = new Dictionary<string, string> { ["text"] = Text, ["visibility"] = "PUBLIC", ["image_path"] = path, ["image_sha256"] = sha };
        if (alt is not null)
        {
            arguments["image_alt"] = alt;
        }

        return _harness.Tool.CallAsync(JsonSerializer.SerializeToElement(arguments), CancellationToken.None);
    }

    /// <summary>An adapter that records the order of its calls and answers from settable outcomes.</summary>
    private sealed class RecordingApi : ILinkedInApi
    {
        public List<string> Calls { get; } = [];

        public byte[]? Uploaded { get; private set; }

        public string? FailInitialize { get; set; }

        public string? FailUpload { get; set; }

        public CreatePostResult PostResult { get; set; } = new(Urn, null, null);

        public Task<TokenExchangeResult> ExchangeCodeAsync(string code, string clientId, ReadOnlyMemory<byte> clientSecret, string redirectUri, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<UserInfoResult> GetUserInfoAsync(string accessToken, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<CreatePostResult> CreatePostAsync(
            string accessToken, string authorUrn, string commentary, PostVisibility visibility, string apiVersion, CancellationToken cancellationToken, PostMedia? media = null)
        {
            Calls.Add($"post {media?.ImageUrn ?? "-"} {media?.AltText ?? "-"}");
            return Task.FromResult(PostResult);
        }

        public Task<ImageUploadStart> InitializeImageUploadAsync(string accessToken, string ownerUrn, string apiVersion, CancellationToken cancellationToken)
        {
            Calls.Add("initialize " + ownerUrn);
            return Task.FromResult(FailInitialize is { } reason
                ? new ImageUploadStart(null, reason)
                : new ImageUploadStart(new ImageUploadTicket("https://www.linkedin.com/dms-uploads/x", ImageUrn), null));
        }

        public Task<string?> UploadImageAsync(string accessToken, string uploadUrl, ReadOnlyMemory<byte> bytes, string mediaType, CancellationToken cancellationToken)
        {
            Calls.Add($"upload {mediaType} {bytes.Length}");
            Uploaded = bytes.ToArray();
            return Task.FromResult(FailUpload);
        }
    }
}
