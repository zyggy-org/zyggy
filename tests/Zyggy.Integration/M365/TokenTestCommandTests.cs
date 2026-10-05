using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Web;

using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.M365;

/// <summary>
/// Step 9 (spec 33 AC-18): <c>zyggy m365 token-test</c> reports the size and expiry of a minted token and never the token itself
/// (deviation 1: the bearer-printing <c>token</c> verb is gone).
/// </summary>
public sealed class TokenTestCommandTests : IDisposable
{
    private readonly M365InstanceFixture _fixture = new();
    private readonly StubGraphHandler _graph = StubGraphHandler.FromGoldenRoutes();
    private readonly TestCertificates _pair;

    public TokenTestCommandTests() => _pair = TestCertificates.Create(Path.Combine(_fixture.Home, ".config", "zyggy"));

    public static bool IsLinux => OperatingSystem.IsLinux();

    public void Dispose()
    {
        _graph.Violations.Should().BeEmpty();
        _pair.Dispose();
        _fixture.Dispose();
    }

    private static string TokenText => System.Text.Json.JsonDocument.Parse(File.ReadAllText(StubGraphHandler.GraphFixture("token-ok.json"))).RootElement.GetProperty("access_token").GetString()!;

    [Fact]
    public async Task TokenTest_PrintsSizeAndExpiryNeverToken()
    {
        // Act
        var (exit, console) = await M365InProcess.RunAsync(_fixture.Env(), _graph, ["token-test"]);

        // Assert
        exit.Should().Be(0, console.Stderr);
        console.Stdout.Should().Be($"token ok: {Encoding.UTF8.GetByteCount(TokenText)} bytes, expires 2026-09-30T10:59:59Z\n");
        console.Stderr.Should().Be("key: file\n");
        (console.Stdout + console.Stderr).Should().NotContain("STUBACCESS").And.NotContain(TokenText);
        _graph.Requests.Should().ContainSingle().Which.Method.Should().Be(HttpMethod.Post);
    }

    [Fact]
    public async Task TokenTest_KeyNew_UsesPendingPair()
    {
        // Arrange
        using var next = TestCertificates.Create(Path.Combine(_fixture.Root, "next"));
        File.Copy(next.KeyFile, _pair.KeyFile + ".new");
        File.Copy(next.CertificateFile, _pair.CertificateFile + ".new");
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(_pair.KeyFile + ".new", UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        // Act
        var (exit, console) = await M365InProcess.RunAsync(_fixture.Env(), _graph, ["token-test", "--key", "new"]);

        // Assert
        exit.Should().Be(0, console.Stderr);
        var parts = Assertion().Split('.');
        using var publicKey = next.Certificate.GetRSAPublicKey()!;
        publicKey.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), FromBase64Url(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pss)
            .Should().BeTrue();
    }

    [Fact]
    public async Task TokenTest_Rs256_HeaderRs256()
    {
        // Act
        var (exit, console) = await M365InProcess.RunAsync(_fixture.Env(), _graph, ["token-test", "--alg", "RS256"]);

        // Assert
        exit.Should().Be(0, console.Stderr);
        Encoding.UTF8.GetString(FromBase64Url(Assertion().Split('.')[0])).Should().StartWith("""{"alg":"RS256","typ":"JWT","x5t":""");
    }

    [Fact]
    public async Task TokenTest_KeyMissing_ExitThree()
    {
        // Arrange
        File.Delete(_pair.KeyFile);

        // Act
        var run = await ZyggyCli.RunAsync(["m365", "token-test"], _fixture.Env(), null, _fixture.Root, TestContext.Current.CancellationToken);

        // Assert
        run.ExitCode.Should().Be(3);
        if (OperatingSystem.IsLinux())
        {
            run.Stderr.Should().Be("m365: key: not found in credentials directory or file\n");
        }
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "Unix file modes: Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task TokenTest_OnLinux_KeyMode0644_ExitThreeShellMessage()
    {
        // Arrange
        File.SetUnixFileMode(_pair.KeyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        // Act
        var run = await ZyggyCli.RunAsync(["m365", "token-test"], _fixture.Env(), null, _fixture.Root, TestContext.Current.CancellationToken);

        // Assert
        run.ExitCode.Should().Be(3);
        run.Stderr.Should().Be($"m365: key: {_pair.KeyFile} must be mode 0600 (is 644)\n");
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "Unix file modes: Linux only")]
    public async Task TokenTest_OnLinux_EcKey_ExitThree()
    {
        // Arrange
        TestCertificates.WriteEcKey(_pair.KeyFile);

        // Act
        var run = await ZyggyCli.RunAsync(["m365", "token-test"], _fixture.Env(), null, _fixture.Root, TestContext.Current.CancellationToken);

        // Assert
        run.ExitCode.Should().Be(3);
        run.Stderr.Should().Be($"key: file\nm365: key: {_pair.KeyFile} is not an RSA key\n");
    }

    private string Assertion() => HttpUtility.ParseQueryString(_graph.Requests.Single(r => r.Method == HttpMethod.Post).Body!)["client_assertion"]!;

    private static byte[] FromBase64Url(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded + new string('=', (4 - (padded.Length % 4)) % 4));
    }
}
