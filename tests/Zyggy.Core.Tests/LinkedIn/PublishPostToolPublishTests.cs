using System.Text.Json;

using Zyggy.Core.LinkedIn;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.LinkedIn;

/// <summary>
/// An accepted text becomes exactly one post (spec 36 AC-2, AC-3, AC-12, AC-22, AC-23): the commentary un-escaped equals the input, the
/// result names the URN and its link, one <c>ok</c> row and one fact; every failure one row, no fact, no retry; the token nowhere else.
/// </summary>
public sealed class PublishPostToolPublishTests : IDisposable
{
    private const string Urn = "urn:li:share:7000000000000000001";
    private const string Text = "Shipped the \"LinkedIn\" tool, see https://digiverse.example/blog today. More soon.\n#dotnet";

    private readonly StubLinkedInHandler _stub = new StubLinkedInHandler()
        .Always("POST", StubLinkedInHandler.PostsUrl, 201, string.Empty, StubLinkedInHandler.Fixture("post-201.headers"))
        .WatchUris(LinkedInFixture.AccessToken);

    private readonly LinkedInHttp _http;
    private readonly PublishHarness _harness;

    public PublishPostToolPublishTests()
    {
        _http = new LinkedInHttp(_stub, TimeSpan.FromMilliseconds(200), null);
        _harness = new PublishHarness(new LinkedInApi(_http, LinkedInEndpoints.Resolve(new Dictionary<string, string?>()).Routes!, PublishHarness.Patterns));
    }

    public static TheoryData<int> Seeds() => [.. Enumerable.Range(1001, 200)];

    public void Dispose()
    {
        _harness.Dispose();
        _http.Dispose();
        _stub.Violations.Should().BeEmpty();
    }

    [Fact]
    public async Task Call_Accepted_OnePost_ResultPublishedUrnLink_OkRowWithTextSha_FactLine()
    {
        // Act
        var result = await _harness.CallAsync(Text);

        // Assert
        result.Should().Be(new ToolCallResult(false, $"published: {Urn} — https://www.linkedin.com/feed/update/{Urn}/"));
        var request = _stub.Requests.Should().ContainSingle().Subject;
        using var body = JsonDocument.Parse(request.Body!);
        body.RootElement.GetProperty("author").GetString().Should().Be("urn:li:person:sub-alice-0001");
        var row = _harness.Rows().Should().ContainSingle().Subject;
        row.GetRawText().Should().Be(
            "{\"schema\":1,\"ts\":\"2026-10-07T08:00:00Z\",\"tool\":\"publish_post\",\"urn\":\"" + Urn + "\",\"visibility\":\"PUBLIC\",\"chars\":" + Text.Length
            + ",\"sha256\":\"" + PublishPostTool.Sha256(Text) + "\",\"text\":\"Shipped the \\\"LinkedIn\\\" tool, see https://digiverse.example/blog today. More soon.\\n#dotnet\",\"status\":\"ok\"}");
        File.ReadAllText(_harness.Memory.Full("inbox/linkedin-2026-10-07.md"))
            .Should().EndWith($"- [observed] 2026-10-07 (linkedin {Urn}): Posted on LinkedIn (PUBLIC): \"Shipped the 'LinkedIn' tool, see [link] today.\"\n");
        _harness.Diagnostics.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task Call_ApprovedTextProperty_CommentaryUnescapedEqualsInput(int seed)
    {
        // Arrange: a random text that passes the local checks (letters first, so it is never whitespace only)
        var text = "Post " + LittleTextTests.RandomText(seed);

        // Act
        var result = await _harness.CallAsync(text);

        // Assert
        result.IsError.Should().BeFalse(result.Text);
        using var body = JsonDocument.Parse(_stub.Requests.Single().Body!);
        LittleText.Unescape(body.RootElement.GetProperty("commentary").GetString()!).Should().Be(text);
    }

    [Fact]
    public async Task Call_RowSha256IsOfTheInputText()
    {
        // Act
        await _harness.CallAsync(Text);

        // Assert
        var expected = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Text)));
        _harness.Rows().Single().GetProperty("sha256").GetString().Should().Be(expected);
    }

    [Theory]
    [InlineData(401, "{}", "token_expired: reconnect LinkedIn — runbook \"LinkedIn token expired\"")]
    [InlineData(403, "{}", "forbidden: reconnect LinkedIn — runbook \"Publish refused or failed\"")]
    [InlineData(426, "{}", "version_retired: api_version 202609 retired — runbook \"LinkedIn API version retired\"")]
    [InlineData(429, "{}", "rate_limited: try again later")]
    [InlineData(400, """{"message":"FIELD_LENGTH_TOO_LONG"}""", "rejected: FIELD_LENGTH_TOO_LONG")]
    [InlineData(500, "{}", "outcome_unknown: the post may exist — check your profile before asking again")]
    [InlineData(0, "reset", "outcome_unknown: the post may exist — check your profile before asking again")]
    [InlineData(0, "timeout", "outcome_unknown: the post may exist — check your profile before asking again")]
    public async Task Call_Failure_OneRowNoFactNoRetry(int status, string body, string expected)
    {
        // Arrange
        if (body == "reset")
        {
            _stub.OnceThrow("POST", StubLinkedInHandler.PostsUrl);
        }
        else if (body == "timeout")
        {
            _stub.OnceDelayed("POST", StubLinkedInHandler.PostsUrl, TimeSpan.FromSeconds(5));
        }
        else
        {
            _stub.Once("POST", StubLinkedInHandler.PostsUrl, status, body);
        }

        // Act
        var result = await _harness.CallAsync(Text);

        // Assert
        result.Should().Be(new ToolCallResult(true, expected));
        _stub.Requests.Should().ContainSingle();
        var row = _harness.Rows().Should().ContainSingle().Subject;
        row.GetProperty("status").GetString().Should().Be(expected);
        row.GetProperty("urn").ValueKind.Should().Be(JsonValueKind.Null);
        _harness.MemoryFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task Call_FactRefused_PostStays_ResultSuffix_StderrLine()
    {
        // Arrange: a bracketed area code passes the post's phone rule but not the fact validator's
        const string Numbers = "Call (012) 345 6789 today.";

        // Act
        var result = await _harness.CallAsync(Numbers);

        // Assert
        result.Should().Be(new ToolCallResult(false, $"published: {Urn} — https://www.linkedin.com/feed/update/{Urn}/; fact not recorded: phone"));
        _harness.Diagnostics.Should().Be("linkedin: fact not recorded: phone\n");
        _harness.Rows().Single().GetProperty("status").GetString().Should().Be("ok");
        _harness.MemoryFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task Call_NoDraftAnywhere()
    {
        // Act
        await _harness.CallAsync(Text);
        _stub.Once("POST", StubLinkedInHandler.PostsUrl, 500);
        await _harness.CallAsync("A second, different text.");

        // Assert
        _harness.MemoryFiles().Should().Equal("inbox/linkedin-2026-10-07.md");
        File.ReadAllText(_harness.Memory.Full("inbox/linkedin-2026-10-07.md")).Should().NotContain("second");
    }

    [Fact]
    public async Task Call_SecretScan_TokenNeverInResultRowFactOrStderr()
    {
        // Act
        var results = new List<ToolCallResult> { await _harness.CallAsync(Text) };
        _stub.Once("POST", StubLinkedInHandler.PostsUrl, 400, "{\"message\":\"token " + LinkedInFixture.AccessToken + "\"}");
        results.Add(await _harness.CallAsync("Another text."));

        // Assert
        var all = string.Join('\n', results.Select(r => r.Text)) + _harness.Diagnostics + File.ReadAllText(_harness.Log.FilePath)
            + File.ReadAllText(_harness.Memory.Full("inbox/linkedin-2026-10-07.md"));
        all.Should().NotContain(LinkedInFixture.AccessToken);
    }
}
