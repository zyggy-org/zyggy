using System.Text.RegularExpressions;

using Zyggy.Core.LinkedIn;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.LinkedIn;

/// <summary>
/// <c>zyggy linkedin auth finish</c> (spec 36 AC-17): the pasted address is checked in the plan's order before one exchange and one user
/// info; another account than the pinned one is refused; the token is stored; the code, the client secret and the token never printed.
/// </summary>
public sealed partial class AuthFinishVerbTests : IDisposable
{
    private const string Code = "code-test-0001-authorization-code";
    private const string Redirect = "https://localhost/zyggy/linkedin";

    private readonly LinkedInFixture _fixture = new();
    private readonly StubLinkedInHandler _stub = StubLinkedInHandler.SignInOk().WatchUris(LinkedInFixture.AccessToken, LinkedInFixture.ClientSecret);

    public void Dispose()
    {
        _fixture.Dispose();
        _stub.Violations.Should().BeEmpty();
    }

    [Fact]
    public async Task Run_ValidAddress_OneExchangeOneUserinfo_TokenStored_PendingDeleted_StdoutConnected()
    {
        // Arrange
        _fixture.WriteClientSecret();
        var state = await StartAsync();
        var console = new VerbConsole($"{Redirect}?code={Code}&state={state}\n");

        // Act
        var exit = await FinishAsync(console);

        // Assert
        exit.Should().Be(0);
        console.Stdout.Should().Be("connected: Alice Example, expires 2026-12-06\n");
        console.Stderr.Should().BeEmpty();
        var token = LinkedInToken.TryParse(File.ReadAllBytes(_fixture.Paths.TokenFile))!;
        token.Should().Be(new LinkedInToken(
            1, LinkedInFixture.AccessToken, LinkedInFixture.Now.AddSeconds(5184000), "openid,profile,w_member_social", "sub-alice-0001", "Alice Example", LinkedInFixture.Now));
        File.Exists(_fixture.Paths.PendingAuth).Should().BeFalse();
        var exchange = _stub.To(StubLinkedInHandler.TokenUrl).Should().ContainSingle().Subject;
        exchange.Body.Should().Be(
            $"grant_type=authorization_code&code={Code}&client_id=clientid0001&client_secret={LinkedInFixture.ClientSecret}&redirect_uri=https%3A%2F%2Flocalhost%2Fzyggy%2Flinkedin");
        var userinfo = _stub.To(StubLinkedInHandler.UserInfoUrl).Should().ContainSingle().Subject;
        userinfo.Headers["Authorization"].Should().Be("Bearer " + LinkedInFixture.AccessToken);
        _stub.Requests.Should().HaveCount(2);
    }

    [Theory]
    [InlineData("no pending", "no pending connection — say \"connect LinkedIn\"", false)]
    [InlineData("other origin", "not the registered redirect address", true)]
    [InlineData("other path", "not the registered redirect address", true)]
    [InlineData("not an address", "not the registered redirect address", true)]
    [InlineData("cancelled", "sign-in cancelled (user_cancelled_authorize)", false)]
    [InlineData("state mismatch", "state mismatch", false)]
    [InlineData("stale", "sign-in link expired", false)]
    [InlineData("no code", "no code in the address", false)]
    public async Task Run_Refusals_ExitFiveNothingStored(string scenario, string refusal, bool pendingKept)
    {
        // Arrange
        _fixture.WriteClientSecret();
        var state = scenario == "no pending" ? "s" : await StartAsync();
        var line = scenario switch
        {
            "other origin" => $"https://evil.example/zyggy/linkedin?code={Code}&state={state}",
            "other path" => $"https://localhost/other?code={Code}&state={state}",
            "not an address" => "connected!",
            "cancelled" => $"{Redirect}?error=user_cancelled_authorize&error_description=The+user+cancelled&state={state}",
            "state mismatch" => $"{Redirect}?code={Code}&state=forged{state}",
            "no code" => $"{Redirect}?state={state}",
            _ => $"{Redirect}?code={Code}&state={state}",
        };
        if (scenario == "stale")
        {
            _fixture.Clock.Advance(TimeSpan.FromMinutes(30) + TimeSpan.FromSeconds(1));
        }

        var console = new VerbConsole(line + "\n");

        // Act
        var exit = await FinishAsync(console);

        // Assert
        exit.Should().Be(5);
        console.Stdout.Should().Be($"refused: {refusal}\n");
        File.Exists(_fixture.Paths.TokenFile).Should().BeFalse();
        File.Exists(_fixture.Paths.PendingAuth).Should().Be(pendingKept);
        _stub.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Run_Exactly30Minutes_Accepted()
    {
        // Arrange
        _fixture.WriteClientSecret();
        var state = await StartAsync();
        _fixture.Clock.Advance(TimeSpan.FromMinutes(30));

        // Act
        var exit = await FinishAsync(new VerbConsole($"{Redirect}?code={Code}&state={state}\n"));

        // Assert
        exit.Should().Be(0);
    }

    [Fact]
    public async Task Run_PinnedSubDiffers_ExitFiveNoTokenWritten()
    {
        // Arrange
        Pin("sub-bob-0002");
        _fixture.WriteClientSecret();
        var state = await StartAsync();
        var console = new VerbConsole($"{Redirect}?code={Code}&state={state}\n");

        // Act
        var exit = await FinishAsync(console);

        // Assert
        exit.Should().Be(5);
        console.Stdout.Should().Be("refused: a different LinkedIn account\n");
        File.Exists(_fixture.Paths.TokenFile).Should().BeFalse();
    }

    [Fact]
    public async Task Run_PinnedSubEqual_Stored()
    {
        // Arrange
        Pin("sub-alice-0001");
        _fixture.WriteClientSecret();
        var state = await StartAsync();

        // Act
        var exit = await FinishAsync(new VerbConsole($"{Redirect}?code={Code}&state={state}\n"));

        // Assert
        exit.Should().Be(0);
        File.Exists(_fixture.Paths.TokenFile).Should().BeTrue();
    }

    [Fact]
    public async Task Run_NotPinned_Stored()
    {
        // Arrange
        _fixture.WriteClientSecret();
        var state = await StartAsync();

        // Act
        var exit = await FinishAsync(new VerbConsole($"{Redirect}?code={Code}&state={state}\n"));

        // Assert
        exit.Should().Be(0);
        LinkedInToken.TryParse(File.ReadAllBytes(_fixture.Paths.TokenFile))!.Sub.Should().Be("sub-alice-0001");
    }

    [Fact]
    public async Task Run_ClientSecretMissing_ExitThreeNamesPath()
    {
        // Arrange
        var state = await StartAsync();
        var console = new VerbConsole($"{Redirect}?code={Code}&state={state}\n");

        // Act
        var exit = await FinishAsync(console);

        // Assert
        exit.Should().Be(3);
        console.Stderr.Should().Be($"linkedin: no client secret at {_fixture.Paths.ClientSecretFile} — runbook \"Install the LinkedIn client secret\"\n");
        _stub.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Run_ExchangeError_ExitSixErrorCodeOnly()
    {
        // Arrange
        _fixture.WriteClientSecret();
        _stub.Once("POST", StubLinkedInHandler.TokenUrl, 400, StubLinkedInHandler.Fixture("token-error.json"));
        var state = await StartAsync();
        var console = new VerbConsole($"{Redirect}?code={Code}&state={state}\n");

        // Act
        var exit = await FinishAsync(console);

        // Assert
        exit.Should().Be(6);
        console.Stderr.Should().Be("linkedin: token exchange failed (invalid_grant)\n");
        File.Exists(_fixture.Paths.TokenFile).Should().BeFalse();
        _stub.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Run_UserinfoFails_ExitSixNoTokenWritten()
    {
        // Arrange
        _fixture.WriteClientSecret();
        _stub.Once("GET", StubLinkedInHandler.UserInfoUrl, 401);
        var state = await StartAsync();
        var console = new VerbConsole($"{Redirect}?code={Code}&state={state}\n");

        // Act
        var exit = await FinishAsync(console);

        // Assert
        exit.Should().Be(6);
        console.Stderr.Should().Be("linkedin: account lookup failed\n");
        File.Exists(_fixture.Paths.TokenFile).Should().BeFalse();
    }

    [Fact]
    public async Task Run_SecretScan_CodeSecretTokenNeverInStdoutStderr()
    {
        // Arrange: one success, one failed exchange, one failed lookup
        _fixture.WriteClientSecret();
        var outputs = new List<string>();
        foreach (var setup in new Action[]
        {
            () => { },
            () => _stub.Once("POST", StubLinkedInHandler.TokenUrl, 400, StubLinkedInHandler.Fixture("token-error.json")),
            () => _stub.Once("GET", StubLinkedInHandler.UserInfoUrl, 500),
        })
        {
            setup();
            var state = await StartAsync();
            var console = new VerbConsole($"{Redirect}?code={Code}&state={state}\n");

            // Act
            await FinishAsync(console);
            outputs.Add(console.Stdout + console.Stderr);
        }

        // Assert
        var all = string.Join('\n', outputs) + _fixture.EverythingOutsideCredentials();
        all.Should().NotContain(Code).And.NotContain(LinkedInFixture.ClientSecret).And.NotContain(LinkedInFixture.AccessToken);
    }

    [Fact]
    public async Task Run_ArgumentGiven_ExitFour()
    {
        // Act
        var exit = await _fixture.Host(_stub).RunAsync(["auth", "finish", Redirect], new VerbConsole(string.Empty).Io, CancellationToken.None);

        // Assert
        exit.Should().Be(4);
    }

    [Fact]
    public async Task Run_LineTooLong_ExitFour()
    {
        // Act
        var exit = await FinishAsync(new VerbConsole(new string('a', 4097) + "\n"));

        // Assert
        exit.Should().Be(4);
    }

    private void Pin(string sub) =>
        _fixture.WriteInstance($$"""{"client_id":"clientid0001","redirect_uri":"{{Redirect}}","member_sub":"{{sub}}"}""");

    private async Task<string> StartAsync()
    {
        var console = new VerbConsole();
        (await _fixture.Host().RunAsync(["auth", "start"], console.Io, CancellationToken.None)).Should().Be(0);
        return StateParameter().Match(console.Stdout).Groups[1].Value;
    }

    private Task<int> FinishAsync(VerbConsole console) => _fixture.Host(_stub).RunAsync(["auth", "finish"], console.Io, CancellationToken.None);

    [GeneratedRegex("[?&]state=([^&\n]*)")]
    private static partial Regex StateParameter();
}
