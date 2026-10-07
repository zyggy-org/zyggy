using NSubstitute;

using Zyggy.Core.LinkedIn;

namespace Zyggy.Core.Tests.LinkedIn;

/// <summary>
/// Everything the handler refuses before any request (spec 36 AC-4, AC-5, AC-20, AC-23): malformed arguments, a text that must not be
/// posted, publishing switched off, no or an expired token, a missing scope, a duplicate within 24 h — each with one row and no request.
/// </summary>
public sealed class PublishPostToolRefusalTests : IDisposable
{
    private readonly ILinkedInApi _api = Substitute.For<ILinkedInApi>();
    private readonly PublishHarness _harness;

    public PublishPostToolRefusalTests()
    {
        _api.CreatePostAsync(default!, default!, default!, default, default!, default)
            .ReturnsForAnyArgs(new CreatePostResult("urn:li:share:7000000000000000001", null, null));
        _harness = new PublishHarness(_api);
    }

    public void Dispose() => _harness.Dispose();

    [Theory]
    [InlineData("", "refused: empty", true)]
    [InlineData("   ", "refused: empty", true)]
    [InlineData("tab\there", "refused: control character", false)]
    [InlineData("Pay to BE71 0961 2345 6769", "refused: secret pattern iban", false)]
    [InlineData("Mail me: someone@domain.example", "refused: e-mail address", false)]
    [InlineData("Call +32 470 12 34 56", "refused: phone number", false)]
    public async Task Call_LocalRefusal_IsErrorNoApiCallOneRow(string text, string expected, bool textKept)
    {
        // Act
        var result = await _harness.CallAsync(text);

        // Assert
        result.Should().Be(new ToolCallResult(true, expected));
        _api.ReceivedCalls().Should().BeEmpty();
        var row = _harness.Rows().Should().ContainSingle().Subject;
        row.GetProperty("status").GetString().Should().Be(expected);
        row.GetProperty("urn").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        if (textKept)
        {
            row.GetProperty("text").GetString().Should().Be(text);
        }
        else
        {
            row.GetProperty("text").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        }
    }

    [Fact]
    public async Task Call_TooLong_RefusedNamesLength()
    {
        // Act
        var result = await _harness.CallAsync(new string('x', 3001));

        // Assert
        result.Text.Should().Be("refused: too long (3001 > 3000)");
        _api.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Call_InvalidArguments_RefusedOneRow()
    {
        // Act
        var result = await _harness.Tool.CallAsync(System.Text.Json.JsonDocument.Parse("""{"text":"x"}""").RootElement, CancellationToken.None);

        // Assert
        result.Should().Be(new ToolCallResult(true, "refused: invalid arguments"));
        _harness.Rows().Should().ContainSingle().Which.GetProperty("visibility").GetString().Should().BeEmpty();
        _api.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Call_SwitchedOff_RefusedOneRow()
    {
        // Arrange
        _harness.Configuration = PublishHarness.Load("""{"client_id":"abc","redirect_uri":"https://localhost/cb","actions":{"enabled":[]}}""");

        // Act
        var result = await _harness.CallAsync("Hello");

        // Assert
        result.Should().Be(new ToolCallResult(true, "refused: publishing switched off"));
        _harness.Rows().Should().ContainSingle();
        _api.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Call_NotConnected_NotConnectedOneRow()
    {
        // Arrange
        _harness.SetToken(null);

        // Act
        var result = await _harness.CallAsync("Hello");

        // Assert
        result.Should().Be(new ToolCallResult(true, "not_connected: say \"connect LinkedIn\""));
        _harness.Rows().Should().ContainSingle().Which.GetProperty("status").GetString().Should().Be("not_connected: say \"connect LinkedIn\"");
        _api.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Call_TokenExpired_TokenExpiredNoApiCall()
    {
        // Arrange
        _harness.SetToken(LinkedInFixture.Token(LinkedInFixture.Now.AddDays(1)));
        _harness.Clock.Advance(TimeSpan.FromDays(1));

        // Act
        var result = await _harness.CallAsync("Hello");

        // Assert
        result.Should().Be(new ToolCallResult(true, "token_expired: reconnect LinkedIn — runbook \"LinkedIn token expired\""));
        _harness.Rows().Should().ContainSingle();
        _api.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Call_ScopeMissing_ForbiddenNoApiCall()
    {
        // Arrange
        _harness.SetToken(LinkedInFixture.Token(LinkedInFixture.Now.AddDays(60), scope: "openid,profile"));

        // Act
        var result = await _harness.CallAsync("Hello");

        // Assert
        result.IsError.Should().BeTrue();
        result.Text.Should().StartWith("forbidden: reconnect LinkedIn");
        _api.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Call_DuplicateWithin24h_RefusedNamesUrnAndTime()
    {
        // Arrange
        (await _harness.CallAsync("Hello")).IsError.Should().BeFalse();
        _api.ClearReceivedCalls();
        _harness.Clock.Advance(TimeSpan.FromHours(23));

        // Act
        var result = await _harness.CallAsync("Hello", "CONNECTIONS");

        // Assert: the first was posted 08:00 UTC = 10:00 Brussels
        result.Should().Be(new ToolCallResult(true, "refused: duplicate of urn:li:share:7000000000000000001 posted 2026-10-07 10:00"));
        _api.ReceivedCalls().Should().BeEmpty();
        _harness.Rows().Should().HaveCount(2);
    }

    [Fact]
    public async Task Call_DuplicateAfter24h_ReachesApi()
    {
        // Arrange
        await _harness.CallAsync("Hello");
        _api.ClearReceivedCalls();
        _harness.Clock.Advance(TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1));

        // Act
        var result = await _harness.CallAsync("Hello");

        // Assert
        result.IsError.Should().BeFalse();
        _api.ReceivedCalls().Should().ContainSingle();
    }

    [Fact]
    public async Task Call_Refused_NothingWrittenToMemory()
    {
        // Act
        await _harness.CallAsync("Call +32 470 12 34 56");
        await _harness.CallAsync(string.Empty);
        _harness.SetToken(null);
        await _harness.CallAsync("Hello");

        // Assert
        _harness.MemoryFiles().Should().BeEmpty();
        _harness.Rows().Should().HaveCount(3);
    }

    [Theory]
    [InlineData("Pay to BE71 0961 2345 6769", "BE71")]
    [InlineData("Mail me: someone@domain.example", "someone@")]
    [InlineData("Call +32 470 12 34 56", "470 12")]
    public async Task Call_ResultTextNeverEchoesMatchedValue(string text, string fragment)
    {
        // Act
        var result = await _harness.CallAsync(text);

        // Assert
        result.Text.Should().NotContain(fragment);
        File.ReadAllText(_harness.Log.FilePath).Should().NotContain(fragment);
    }
}
