using System.Buffers.Text;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

using Zyggy.Core.LinkedIn;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.LinkedIn;

/// <summary><c>zyggy linkedin auth start</c> (spec 36 AC-16): one sign-in link, a fresh one-time state remembered 0600.</summary>
public sealed partial class AuthStartVerbTests : IDisposable
{
    private readonly LinkedInFixture _fixture = new();

    public static bool IsLinux => OperatingSystem.IsLinux();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Run_PrintsOneUrlByteEqualGoldenExceptState_ExitZero()
    {
        // Arrange
        var console = new VerbConsole();
        var golden = File.ReadAllText(Path.Combine(Golden.Directory, "linkedin", "auth-start-url.txt"));

        // Act
        var exit = await _fixture.Host().RunAsync(["auth", "start"], console.Io, CancellationToken.None);

        // Assert
        exit.Should().Be(0);
        console.Stderr.Should().BeEmpty();
        var state = State(console.Stdout);
        console.Stdout.Should().Be(golden.Replace("<state>", state, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_State32BytesBase64Url_FreshEachRun()
    {
        // Arrange
        var first = new VerbConsole();
        var second = new VerbConsole();

        // Act
        await _fixture.Host().RunAsync(["auth", "start"], first.Io, CancellationToken.None);
        await _fixture.Host().RunAsync(["auth", "start"], second.Io, CancellationToken.None);

        // Assert
        var a = State(first.Stdout);
        var b = State(second.Stdout);
        a.Should().MatchRegex("^[A-Za-z0-9_-]{43}$");
        Base64Url.DecodeFromChars(a).Should().HaveCount(32);
        b.Should().NotBe(a);
        OAuthPending.Read(_fixture.Paths)!.State.Should().Be(b);
    }

    [Fact]
    public async Task Run_PendingFile_StateAndCreated()
    {
        // Arrange
        var console = new VerbConsole();

        // Act
        await _fixture.Host().RunAsync(["auth", "start"], console.Io, CancellationToken.None);

        // Assert
        var json = File.ReadAllText(_fixture.Paths.PendingAuth);
        json.Should().Be($"{{\"state\":\"{State(console.Stdout)}\",\"created\":\"2026-10-07T08:00:00Z\"}}");
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "Unix file modes: Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task Run_PendingFile_StateAndCreated_0600_OnLinux()
    {
        // Act
        await _fixture.Host(checkOwnership: true).RunAsync(["auth", "start"], new VerbConsole().Io, CancellationToken.None);

        // Assert
        File.GetUnixFileMode(_fixture.Paths.PendingAuth).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.GetUnixFileMode(_fixture.Paths.StateDirectory).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Fact]
    public async Task Run_ConfigMissing_ExitThree()
    {
        // Arrange
        File.Delete(_fixture.InstanceFile);
        var console = new VerbConsole();

        // Act
        var exit = await _fixture.Host().RunAsync(["auth", "start"], console.Io, CancellationToken.None);

        // Assert
        exit.Should().Be(3);
        console.Stdout.Should().BeEmpty();
        console.Stderr.Should().Be($"linkedin: configuration error: {_fixture.InstanceFile} is missing\n");
        File.Exists(_fixture.Paths.PendingAuth).Should().BeFalse();
    }

    [Fact]
    public async Task Run_ExtraArgument_ExitFour()
    {
        // Arrange
        var console = new VerbConsole();

        // Act
        var exit = await _fixture.Host().RunAsync(["auth", "start", "--now"], console.Io, CancellationToken.None);

        // Assert
        exit.Should().Be(4);
        console.Stdout.Should().BeEmpty();
        File.Exists(_fixture.Paths.PendingAuth).Should().BeFalse();
    }

    private static string State(string stdout) => StateParameter().Match(stdout).Groups[1].Value;

    [GeneratedRegex("[?&]state=([^&\n]*)")]
    private static partial Regex StateParameter();
}
