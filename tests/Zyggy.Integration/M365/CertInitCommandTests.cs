using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.M365;

/// <summary>
/// Step 9 (spec 33 AC-19): <c>zyggy m365 cert-init</c> from the built binary — the key pair on disk with the shell's modes, refusals,
/// rotation and commit; public material only on stdout.
/// </summary>
public sealed class CertInitCommandTests : IDisposable
{
    private readonly M365InstanceFixture _fixture = new();

    public static bool IsLinux => OperatingSystem.IsLinux();

    public void Dispose() => _fixture.Dispose();

    private string KeyDirectory => Path.Combine(_fixture.Home, ".config", "zyggy");

    private string KeyFile => Path.Combine(KeyDirectory, "m365-app.key");

    private string CertificateFile => Path.Combine(KeyDirectory, "m365-app.cer");

    private Task<ZyggyRun> CertInit(Dictionary<string, string?> env, params string[] args) =>
        ZyggyCli.RunAsync(["m365", "cert-init", .. args], env, null, _fixture.Root, TestContext.Current.CancellationToken);

    [Fact]
    public async Task CertInit_HooksOff_ExitFiveNothingCreated()
    {
        // Arrange
        var env = _fixture.Env();
        env["ZYGGY_HOOKS"] = "off";

        // Act
        var run = await CertInit(env);

        // Assert
        run.ExitCode.Should().Be(5);
        run.Stderr.Should().Be("m365: refused: unattended run (ZYGGY_HOOKS=off)\n");
        Directory.Exists(KeyDirectory).Should().BeFalse();
    }

    [Fact]
    public async Task CertInit_BothModes_ExitFour()
    {
        // Act
        var run = await CertInit(_fixture.Env(), "--rotate", "--commit");

        // Assert
        run.ExitCode.Should().Be(4);
        run.Stderr.Should().StartWith("m365: cert-init takes --rotate or --commit, not both (usage: zyggy m365 cert-init [--rotate|--commit])");
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "key files with Unix modes: Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task CertInit_OnLinux_CreatesKey0600Dir0700Cer0644CnSubject()
    {
        // Act
        var run = await CertInit(_fixture.Env());

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        File.GetUnixFileMode(KeyDirectory).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.GetUnixFileMode(KeyFile).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.GetUnixFileMode(CertificateFile).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        File.ReadAllText(KeyFile).Should().StartWith("-----BEGIN PRIVATE KEY-----\n");
        using var certificate = X509Certificate2.CreateFromPem(File.ReadAllText(CertificateFile));
        certificate.Subject.Should().Be("CN=zyggy-central");
        (certificate.NotAfter.ToUniversalTime() - DateTime.UtcNow).TotalDays.Should().BeApproximately(398, 1);
        var lines = run.Stdout.Split('\n');
        lines[0].Should().Be("thumbprint sha1: " + certificate.Thumbprint);
        lines[1].Should().Be("thumbprint sha256: " + Convert.ToHexString(certificate.GetCertHash(System.Security.Cryptography.HashAlgorithmName.SHA256)));
        lines[2].Should().Be("expires: " + certificate.NotAfter.ToUniversalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        lines[3].Should().Be("certificate: " + CertificateFile);
        run.Stdout.Should().NotContain("PRIVATE KEY");
        run.Stderr.Should().NotContain("PRIVATE KEY");
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "key files with Unix modes: Linux only")]
    public async Task CertInit_OnLinux_KeyExists_ExitFiveUntouched()
    {
        // Arrange
        await CertInit(_fixture.Env());
        var before = File.ReadAllBytes(KeyFile);

        // Act
        var run = await CertInit(_fixture.Env());

        // Assert
        run.ExitCode.Should().Be(5);
        run.Stderr.Should().Be($"m365: refused: key exists — use --rotate ({KeyFile})\n");
        File.ReadAllBytes(KeyFile).Should().Equal(before);
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "key files with Unix modes: Linux only")]
    public async Task CertInit_OnLinux_RotateThenPendingRefusedThenCommitSwaps()
    {
        // Arrange
        await CertInit(_fixture.Env());

        // Act
        var rotate = await CertInit(_fixture.Env(), "--rotate");
        var pending = await CertInit(_fixture.Env(), "--rotate");
        var newKey = File.ReadAllBytes(KeyFile + ".new");
        var commit = await CertInit(_fixture.Env(), "--commit");

        // Assert
        rotate.ExitCode.Should().Be(0, rotate.Stderr);
        rotate.Stdout.Should().Contain("certificate: " + CertificateFile + ".new");
        pending.ExitCode.Should().Be(5);
        pending.Stderr.Should().Be($"m365: refused: a rotation is pending — use --commit or remove {KeyFile}.new\n");
        commit.ExitCode.Should().Be(0, commit.Stderr);
        File.ReadAllBytes(KeyFile).Should().Equal(newKey);
        File.Exists(KeyFile + ".new").Should().BeFalse();
        File.Exists(CertificateFile + ".new").Should().BeFalse();
        commit.Stdout.Should().Contain("certificate: " + CertificateFile + "\n");
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "Linux only")]
    public async Task CertInit_OnLinux_CommitWithoutNew_ExitFour()
    {
        // Act
        var run = await CertInit(_fixture.Env(), "--commit");

        // Assert
        run.ExitCode.Should().Be(4);
        run.Stderr.Should().Be($"m365: --commit: no {KeyFile}.new to commit (usage: zyggy m365 cert-init [--rotate|--commit])\n");
    }

    [Fact]
    public async Task CertInit_NotLinux_ExitThree()
    {
        // Act
        var run = await CertInit(_fixture.Env());

        // Assert
        if (!OperatingSystem.IsLinux())
        {
            run.ExitCode.Should().Be(3);
            run.Stderr.Should().Be("m365: not supported on this platform\n");
        }
        else
        {
            run.ExitCode.Should().Be(0, run.Stderr);
        }
    }
}
