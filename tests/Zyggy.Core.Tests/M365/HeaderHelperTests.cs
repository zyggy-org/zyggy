using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Zyggy.Core.M365.Graph;
using Zyggy.Core.M365.Mcp;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.M365;

/// <summary>
/// The headersHelper — <c>mcp-auth-header.sh</c> (spec 23 D8, spec 33 AC-24): one fresh token per connection, printed as the one JSON
/// line Claude Code reads; one retry inside 8 s; a fixed stderr sentence on failure; the journal never holds the token.
/// </summary>
public sealed class HeaderHelperTests
{
    private const string Token = "eyJhbGciOiJQUzI1NiJ9.eyJhdWQiOiJncmFwaCJ9.c2lnbmF0dXJl";
    private const string Failure = "m365: token refresh failed — runbook 13 \"Certificate rejected\"";

    private readonly ITokenSource _tokens = Substitute.For<ITokenSource>();
    private readonly RecordingProcessRunner _runner = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Run_Success_OneJsonLineStderrEmptyJournalMinted()
    {
        // Arrange
        _tokens.MintAsync(Arg.Any<TokenRequest>(), Arg.Any<CancellationToken>()).Returns(Minted());

        // Act
        var outcome = await Helper().RunAsync(CancellationToken.None);

        // Assert
        outcome.Should().Be(new HeaderOutcome(0, $$"""{"Authorization":"Bearer {{Token}}"}""", null));
        Journal().Should().Equal("token minted");
    }

    [Fact]
    public async Task Run_FirstFailsSecondOk_OneRetry()
    {
        // Arrange
        _tokens.MintAsync(Arg.Any<TokenRequest>(), Arg.Any<CancellationToken>()).Returns(TokenResult.Failed(6, "request failed (reset)"), Minted());

        // Act
        var outcome = await Helper().RunAsync(CancellationToken.None);

        // Assert
        outcome.Exit.Should().Be(0);
        await _tokens.Received(2).MintAsync(Arg.Any<TokenRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Run_InvalidClientTwice_ExitSixStderrExactJournalReason()
    {
        // Arrange
        _tokens.MintAsync(Arg.Any<TokenRequest>(), Arg.Any<CancellationToken>())
            .Returns(TokenResult.Failed(6, "auth failed (invalid_client) — runbook 13 \"Certificate rejected\""));

        // Act
        var outcome = await Helper().RunAsync(CancellationToken.None);

        // Assert
        outcome.Should().Be(new HeaderOutcome(6, null, Failure));
        await _tokens.Received(2).MintAsync(Arg.Any<TokenRequest>(), Arg.Any<CancellationToken>());
        Journal().Should().Equal("token refresh failed: auth failed (invalid_client) — runbook 13 \"Certificate rejected\"");
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Run_ConfigurationError_NoRetry(int exit)
    {
        // Arrange
        _tokens.MintAsync(Arg.Any<TokenRequest>(), Arg.Any<CancellationToken>()).Returns(TokenResult.Failed(exit, "key: not found in credentials directory or file"));

        // Act
        var outcome = await Helper().RunAsync(CancellationToken.None);

        // Assert
        outcome.Should().Be(new HeaderOutcome(exit, null, Failure));
        await _tokens.Received(1).MintAsync(Arg.Any<TokenRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Run_SlowMint_StopsAt8Seconds()
    {
        // Arrange: a mint that never finishes
        var never = new TaskCompletionSource<TokenResult>();
        _tokens.MintAsync(Arg.Any<TokenRequest>(), Arg.Any<CancellationToken>()).Returns(never.Task);

        // Act
        var run = Helper().RunAsync(CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(8));
        var outcome = await run;

        // Assert
        outcome.Should().Be(new HeaderOutcome(6, null, Failure));
        Journal().Should().Equal("token refresh failed: token mint did not finish within 8s");
    }

    [Fact]
    public async Task Run_NotAToken_ExitSix()
    {
        // Arrange
        _tokens.MintAsync(Arg.Any<TokenRequest>(), Arg.Any<CancellationToken>())
            .Returns(TokenResult.Minted("not a \"token\"", _clock.GetUtcNow().AddHours(1), CredentialSource.File));

        // Act
        var outcome = await Helper().RunAsync(CancellationToken.None);

        // Assert
        outcome.Should().Be(new HeaderOutcome(6, null, Failure));
        Journal().Should().Equal("token refresh failed: the token mint printed no token");
    }

    [Fact]
    public async Task Run_LoggerMissing_StillSucceeds()
    {
        // Arrange
        _tokens.MintAsync(Arg.Any<TokenRequest>(), Arg.Any<CancellationToken>()).Returns(Minted());

        // Act
        var outcome = await new HeaderHelper(_tokens, _runner, _clock, loggerPath: null).RunAsync(CancellationToken.None);

        // Assert
        outcome.Exit.Should().Be(0);
        _runner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Run_LoggerFails_StillSucceeds()
    {
        // Arrange
        _tokens.MintAsync(Arg.Any<TokenRequest>(), Arg.Any<CancellationToken>()).Returns(Minted());
        _runner.Hook = _ => RecordingProcessRunner.Fail(1, "logger: socket");

        // Act
        var outcome = await Helper().RunAsync(CancellationToken.None);

        // Assert
        outcome.Exit.Should().Be(0);
    }

    [Fact]
    public async Task Run_TokenNeverInLoggerArgumentsOrStderr()
    {
        // Arrange
        _tokens.MintAsync(Arg.Any<TokenRequest>(), Arg.Any<CancellationToken>()).Returns(Minted(), TokenResult.Failed(6, "x"), TokenResult.Failed(6, "x"));

        // Act
        await Helper().RunAsync(CancellationToken.None);
        var failed = await Helper().RunAsync(CancellationToken.None);

        // Assert
        _runner.Calls.SelectMany(c => c.Arguments).Should().NotContain(a => a.Contains(Token, StringComparison.Ordinal));
        _runner.Calls.Should().OnlyContain(c => c.Arguments.Take(3).SequenceEqual(LoggerPrefix));
        failed.StderrLine.Should().NotContain(Token);
    }

    private static readonly string[] LoggerPrefix = ["-t", "zyggy-m365", "--"];

    private TokenResult Minted() => TokenResult.Minted(Token, _clock.GetUtcNow().AddHours(1), CredentialSource.File);

    private HeaderHelper Helper() => new(_tokens, _runner, _clock, loggerPath: "/usr/bin/logger");

    private List<string> Journal() => [.. _runner.Calls.Select(c => c.Arguments[^1])];
}
