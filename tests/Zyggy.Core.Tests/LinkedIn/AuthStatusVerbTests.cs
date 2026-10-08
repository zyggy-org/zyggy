using System.Runtime.Versioning;

using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.LinkedIn;

/// <summary><c>zyggy linkedin auth status</c> (spec 36 AC-18): connected until when, the warning, and the three exit-5 cases.</summary>
public sealed class AuthStatusVerbTests : IDisposable
{
    private readonly LinkedInFixture _fixture = new();

    public static bool IsLinux => OperatingSystem.IsLinux();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Run_Connected_ExitZeroLine()
    {
        // Arrange: expires 2026-12-06 08:00 UTC = 10:00 Brussels; today 2026-10-07 → 60 days
        _fixture.WriteToken(LinkedInFixture.Token(new DateTimeOffset(2026, 12, 6, 8, 0, 0, TimeSpan.Zero)));
        var console = new VerbConsole();

        // Act
        var exit = await Status(console);

        // Assert
        exit.Should().Be(0);
        console.Stdout.Should().Be("connected: Alice Example, expires 2026-12-06 (60 days)\n");
        console.Stderr.Should().BeEmpty();
    }

    [Theory]
    [InlineData(7, true)]
    [InlineData(8, false)]
    public async Task Run_DaysLeft7_8_WarnBoundary(int days, bool warns)
    {
        // Arrange
        _fixture.WriteToken(LinkedInFixture.Token(LinkedInFixture.Now.AddDays(days)));
        var console = new VerbConsole();

        // Act
        var exit = await Status(console);

        // Assert
        exit.Should().Be(0);
        var date = LinkedInFixture.Now.AddDays(days).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        console.Stdout.Should().Be(
            $"connected: Alice Example, expires {date} ({days} days)" + (warns ? " — reconnect soon: say \"connect LinkedIn\"" : string.Empty) + "\n");
    }

    [Fact]
    public async Task Run_WarnDaysFromInstance()
    {
        // Arrange
        _fixture.WriteInstance("""{"client_id":"abc","redirect_uri":"https://localhost/cb","expiry_warn_days":10}""");
        _fixture.WriteToken(LinkedInFixture.Token(LinkedInFixture.Now.AddDays(9)));
        var console = new VerbConsole();

        // Act
        await Status(console);

        // Assert
        console.Stdout.Should().EndWith(" — reconnect soon: say \"connect LinkedIn\"\n");
    }

    [Fact]
    public async Task Run_NotConnected_ExitFive()
    {
        // Arrange
        var console = new VerbConsole();

        // Act
        var exit = await Status(console);

        // Assert
        exit.Should().Be(5);
        console.Stdout.Should().Be("not connected — runbook \"Connect LinkedIn\"\n");
    }

    [Fact]
    public async Task Run_Expired_ExitFive()
    {
        // Arrange
        _fixture.WriteToken(LinkedInFixture.Token(LinkedInFixture.Now.AddDays(1)));
        _fixture.Clock.Advance(TimeSpan.FromDays(1));
        var console = new VerbConsole();

        // Act
        var exit = await Status(console);

        // Assert
        exit.Should().Be(5);
        console.Stdout.Should().Be("expired 2026-10-08 — say \"connect LinkedIn\"\n");
    }

    [Fact]
    public async Task Run_ScopeWithoutWMemberSocial_ExitFiveNamesScope()
    {
        // Arrange
        _fixture.WriteToken(LinkedInFixture.Token(LinkedInFixture.Now.AddDays(60), scope: "openid,profile"));
        var console = new VerbConsole();

        // Act
        var exit = await Status(console);

        // Assert
        exit.Should().Be(5);
        console.Stdout.Should().Be("connected without scope w_member_social — say \"connect LinkedIn\"\n");
    }

    [Fact]
    public async Task Run_TokenFileNotAToken_ExitThreeNamesPath()
    {
        // Arrange
        Directory.CreateDirectory(_fixture.Paths.ConfigDirectory);
        File.WriteAllText(_fixture.Paths.TokenFile, "{\"access_token\":\"" + LinkedInFixture.AccessToken + "\"}");
        var console = new VerbConsole();

        // Act
        var exit = await Status(console);

        // Assert
        exit.Should().Be(3);
        console.Stderr.Should().Be($"linkedin: {_fixture.Paths.TokenFile} is not a token file\n");
        console.Stderr.Should().NotContain(LinkedInFixture.AccessToken);
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "Unix file modes: Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task Run_TokenBadMode_ExitThreeNamesPath_OnLinux()
    {
        // Arrange
        _fixture.WriteToken(LinkedInFixture.Token(LinkedInFixture.Now.AddDays(60)));
        File.SetUnixFileMode(_fixture.Paths.TokenFile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        var console = new VerbConsole();

        // Act
        var exit = await _fixture.Host(checkOwnership: true).RunAsync(["auth", "status"], console.Io, CancellationToken.None);

        // Assert
        exit.Should().Be(3);
        console.Stderr.Should().Be($"linkedin: {_fixture.Paths.TokenFile} must be mode 0600 (is 640)\n");
    }

    [Fact]
    public async Task Run_NeverPrintsToken()
    {
        // Arrange
        _fixture.WriteToken(LinkedInFixture.Token(LinkedInFixture.Now.AddDays(3)));
        var console = new VerbConsole();

        // Act
        await Status(console);

        // Assert
        (console.Stdout + console.Stderr).Should().NotContain(LinkedInFixture.AccessToken);
    }

    [Fact]
    public async Task Run_Argument_ExitFour()
    {
        // Act
        var exit = await _fixture.Host().RunAsync(["auth", "status", "x"], new VerbConsole().Io, CancellationToken.None);

        // Assert
        exit.Should().Be(4);
    }

    private Task<int> Status(VerbConsole console) => _fixture.Host().RunAsync(["auth", "status"], console.Io, CancellationToken.None);
}
