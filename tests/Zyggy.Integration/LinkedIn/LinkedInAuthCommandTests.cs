using System.Runtime.Versioning;
using System.Text.RegularExpressions;

using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.LinkedIn;

/// <summary>
/// The owner's connection from the built binary against the loopback LinkedIn stand-in (spec 36 AC-15..AC-19): <c>auth start</c> →
/// the landed address into <c>auth finish</c> → <c>auth status</c>; the refusals; the files' modes; no secret in any output.
/// </summary>
public sealed partial class LinkedInAuthCommandTests : IAsyncLifetime
{
    private const UnixFileMode File0600 = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode Dir0700 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private readonly LinkedInFixture _fixture = new();
    private readonly StubLinkedInServer _stub = new();

    public static bool IsLinux => OperatingSystem.IsLinux();

    public static bool IsWindows => OperatingSystem.IsWindows();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await _stub.DisposeAsync();
        _fixture.Dispose();
    }

    [Fact]
    public async Task AuthStart_PrintsUrl_PendingWritten_OnLinux0600Dir0700()
    {
        // Act
        var run = await Zyggy(["linkedin", "auth", "start"]);

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        run.Stdout.Should().MatchRegex(
            "^https://www\\.linkedin\\.com/oauth/v2/authorization\\?response_type=code&client_id=clientid0001&redirect_uri=https%3A%2F%2Flocalhost%2Fzyggy%2Flinkedin&state=[A-Za-z0-9_-]{43}&scope=openid%20profile%20w_member_social\n$");
        File.ReadAllText(_fixture.PendingFile).Should().Contain(State(run.Stdout));
        if (OperatingSystem.IsLinux())
        {
            File.GetUnixFileMode(_fixture.PendingFile).Should().Be(File0600);
            File.GetUnixFileMode(_fixture.StateDirectory).Should().Be(Dir0700);
        }

        _stub.Requests.Should().BeEmpty();
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "the credential files: Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task AuthStartThenFinish_OnLinux_TokenFile0600Dir0700_StdoutConnected_StubSawOneExchangeOneUserinfo()
    {
        // Arrange
        _fixture.WriteClientSecret();

        // Act
        var finish = await ConnectAsync();

        // Assert
        finish.ExitCode.Should().Be(0, finish.Stderr);
        finish.Stdout.Should().Be($"connected: Alice Example, expires {DateTimeOffset.UtcNow.AddSeconds(5184000):yyyy-MM-dd}\n");
        File.GetUnixFileMode(_fixture.TokenFile).Should().Be(File0600);
        File.GetUnixFileMode(_fixture.CredentialDirectory).Should().Be(Dir0700);
        File.ReadAllText(_fixture.TokenFile).Should().Contain("\"access_token\":\"" + LinkedInFixture.AccessToken + "\"").And.Contain("\"sub\":\"sub-alice-0001\"");
        File.Exists(_fixture.PendingFile).Should().BeFalse();
        _stub.To(StubLinkedInServer.TokenPath).Should().ContainSingle().Which.Body.Should().Contain("client_secret=" + LinkedInFixture.ClientSecret);
        _stub.To(StubLinkedInServer.UserInfoPath).Should().ContainSingle().Which.Headers["Authorization"].Should().Be("Bearer " + LinkedInFixture.AccessToken);
        _stub.Requests.Should().HaveCount(2);
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "the credential files: Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task AuthFinish_ForgedState_ExitFiveNoTokenFile_OnLinux()
    {
        // Arrange
        _fixture.WriteClientSecret();
        var start = await Zyggy(["linkedin", "auth", "start"]);

        // Act
        var finish = await Zyggy(["linkedin", "auth", "finish"], $"{LinkedInFixture.Redirect}?code={LinkedInFixture.Code}&state=x{State(start.Stdout)}\n");

        // Assert
        finish.ExitCode.Should().Be(5);
        finish.Stdout.Should().Be("refused: state mismatch\n");
        File.Exists(_fixture.TokenFile).Should().BeFalse();
        _stub.Requests.Should().BeEmpty();
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "the credential files: Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task AuthFinish_ClientSecretMissing_ExitThreeNamesPathNotValue_OnLinux()
    {
        // Act
        var finish = await ConnectAsync();

        // Assert
        finish.ExitCode.Should().Be(3);
        finish.Stderr.Should().Be($"linkedin: no client secret at {_fixture.ClientSecretFile} — runbook \"Install the LinkedIn client secret\"\n");
        _stub.Requests.Should().BeEmpty();
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "the credential files: Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task AuthFinish_ClientSecretBadMode_ExitThreeNamesPathNotValue_OnLinux()
    {
        // Arrange
        _fixture.WriteClientSecret();
        File.SetUnixFileMode(_fixture.ClientSecretFile, File0600 | UnixFileMode.OtherRead);

        // Act
        var finish = await ConnectAsync();

        // Assert
        finish.ExitCode.Should().Be(3);
        finish.Stderr.Should().Be($"linkedin: {_fixture.ClientSecretFile} must be mode 0600 (is 604)\n");
        finish.Stderr.Should().NotContain(LinkedInFixture.ClientSecret);
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "the credential files: Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task AuthFinish_CredentialsDirectoryCopyPreferred_OnLinux()
    {
        // Arrange
        _fixture.WriteClientSecret();
        Directory.CreateDirectory(_fixture.CredentialsDirectory);
        var copy = Path.Combine(_fixture.CredentialsDirectory, "linkedin-client-secret");
        File.WriteAllText(copy, "client-secret-from-credentials\n");
        File.SetUnixFileMode(copy, UnixFileMode.UserRead | UnixFileMode.GroupRead);
        var env = Env();
        env["CREDENTIALS_DIRECTORY"] = _fixture.CredentialsDirectory;

        // Act
        var finish = await ConnectAsync(env);

        // Assert
        finish.ExitCode.Should().Be(0, finish.Stderr);
        _stub.To(StubLinkedInServer.TokenPath).Single().Body.Should().Contain("client_secret=client-secret-from-credentials&");
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "the credential files: Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task AuthStatus_AfterFinish_ExitZeroConnected_OnLinux()
    {
        // Arrange
        _fixture.WriteClientSecret();
        (await ConnectAsync()).ExitCode.Should().Be(0);

        // Act
        var status = await Zyggy(["linkedin", "auth", "status"]);

        // Assert
        status.ExitCode.Should().Be(0, status.Stderr);
        status.Stdout.Should().MatchRegex("^connected: Alice Example, expires [0-9]{4}-[0-9]{2}-[0-9]{2} \\((59|60) days\\)\n$");
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "the credential files: Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task AuthStatus_NotConnected_ExitFive_OnLinux()
    {
        // Act
        var status = await Zyggy(["linkedin", "auth", "status"]);

        // Assert
        status.ExitCode.Should().Be(5);
        status.Stdout.Should().Be("not connected — runbook \"Connect LinkedIn\"\n");
    }

    [Fact(SkipUnless = nameof(IsWindows), Skip = "the platform refusal: Windows only")]
    public async Task AuthStatus_OnWindows_ExitThreeNotSupported()
    {
        // Act
        var status = await Zyggy(["linkedin", "auth", "status"]);

        // Assert
        status.ExitCode.Should().Be(3);
        status.Stderr.Should().Be("linkedin: not supported on this platform\n");
    }

    [Fact]
    public async Task ApiBase_NonLoopback_ExitThree()
    {
        // Arrange
        var env = Env();
        env["ZYGGY_LINKEDIN_API_BASE"] = "http://localhost:" + _stub.Port;
        await Zyggy(["linkedin", "auth", "start"]);

        // Act
        var finish = await ZyggyCli.RunAsync(["linkedin", "auth", "finish"], env, LinkedInFixture.Redirect + "?code=c&state=s\n", _fixture.Root, TestContext.Current.CancellationToken);

        // Assert
        finish.ExitCode.Should().Be(3);
        finish.Stderr.Should().Be("linkedin: configuration error: ZYGGY_LINKEDIN_API_BASE must be a loopback address\n");
        _stub.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Linkedin_UnknownVerb_ExitFour()
    {
        // Act
        var run = await Zyggy(["linkedin", "publish", "hello"]);

        // Assert
        run.ExitCode.Should().Be(4);
        run.Stderr.Should().Be("linkedin: unknown verb 'publish' (usage: zyggy linkedin <auth start|auth finish|auth status|mcp-server>)\n");
    }

    [Fact]
    public async Task Help_ListsLinkedin()
    {
        // Act
        var run = await Zyggy(["--help"]);

        // Assert
        run.ExitCode.Should().Be(0);
        run.Stdout.Should().Contain("linkedin").And.Contain("LinkedIn on Central: connect (auth start|finish|status) and the publishing server (mcp-server).");
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "the credential files: Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task AllOutputs_SecretScan_NoCodeSecretOrToken_OnLinux()
    {
        // Arrange: a success, a refusal, a failed exchange, and the status
        _fixture.WriteClientSecret();
        var outputs = new List<ZyggyRun> { await ConnectAsync() };
        outputs.Add(await Zyggy(["linkedin", "auth", "finish"], $"{LinkedInFixture.Redirect}?code={LinkedInFixture.Code}&state=none\n"));
        _stub.Once(StubLinkedInServer.TokenPath, 400, """{"error":"invalid_grant","error_description":"code-test-0001-authorization-code is bad"}""");

        // Act
        outputs.Add(await ConnectAsync());
        outputs.Add(await Zyggy(["linkedin", "auth", "status"]));

        // Assert
        var all = string.Join('\n', outputs.Select(o => o.Stdout + o.Stderr)) + _fixture.EverythingOutsideCredentials();
        all.Should().NotContain(LinkedInFixture.Code).And.NotContain(LinkedInFixture.ClientSecret).And.NotContain(LinkedInFixture.AccessToken);
        outputs[2].ExitCode.Should().Be(6);
    }

    private Dictionary<string, string?> Env() => _fixture.Env(_stub.Port);

    private Task<ZyggyRun> Zyggy(IReadOnlyList<string> args, string? stdin = null, Dictionary<string, string?>? env = null) =>
        ZyggyCli.RunAsync(args, env ?? Env(), stdin, _fixture.Root, TestContext.Current.CancellationToken);

    private async Task<ZyggyRun> ConnectAsync(Dictionary<string, string?>? env = null)
    {
        var start = await Zyggy(["linkedin", "auth", "start"], env: env);
        start.ExitCode.Should().Be(0, start.Stderr);
        return await Zyggy(["linkedin", "auth", "finish"], $"{LinkedInFixture.Redirect}?code={LinkedInFixture.Code}&state={State(start.Stdout)}\n", env);
    }

    private static string State(string stdout) => StateParameter().Match(stdout).Groups[1].Value;

    [GeneratedRegex("[?&]state=([^&\n]*)")]
    private static partial Regex StateParameter();
}
