using System.Text;
using System.Text.RegularExpressions;

using Zyggy.Core.Envelope;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Cli;

/// <summary>Spec 04 AC-31: <c>zyggy secret set</c> reads the value from stdin only and never echoes it.</summary>
public sealed class SecretSetCommandTests : IDisposable
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private readonly ScratchDirectory _scratch = new();

    private readonly string _nodeJson;

    private readonly string _secretsRoot;

    public SecretSetCommandTests()
    {
        _secretsRoot = _scratch.File("secrets");
        _nodeJson = NodeJson.Write(_scratch.File("cfg"), Acme, MachineName.Parse("home-laptop"), KeyId.Parse("acme/1"), _scratch.File("bus"), "https://example.invalid/acme-org/acme-bus.git", _secretsRoot);
    }

    public static bool IsLinux => OperatingSystem.IsLinux();

    public static bool IsWindows => OperatingSystem.IsWindows();

    public void Dispose() => _scratch.Dispose();

    [Fact]
    public async Task SecretSet_HexFromStdin_FileHoldsHexAndStdoutNamesSize()
    {
        // Arrange
        string hex = Convert.ToHexStringLower(TestKeys.Secret1);

        // Act
        ZyggyRun run = await Run(["secret", "set", "hmac/1", "--hex", "--config", _nodeJson], hex + "\n");

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        run.Stdout.Should().MatchRegex(@"^stored acme/hmac/1 \(32 bytes\) fingerprint [0-9a-f]{8}\r?\n$");
        ExpectOnlyTheFileStoreWarning(run.Stderr);
        File.ReadAllText(Path.Combine(_secretsRoot, "acme", "hmac", "1")).Should().Be(hex + "\n");
        SecretSweep.AssertAbsent([run.Stdout, run.Stderr]);
    }

    [Fact]
    public async Task SecretSet_RawFromStdin_HexEncoded()
    {
        // Act
        ZyggyRun run = await Run(["secret", "set", "github-pat", "--config", _nodeJson], TestKeys.TestPat + "\n");

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        run.Stdout.Should().MatchRegex($@"^stored acme/github-pat \({TestKeys.TestPat.Length} bytes\) fingerprint [0-9a-f]{{8}}\r?\n$");
        ExpectOnlyTheFileStoreWarning(run.Stderr);
        File.ReadAllText(Path.Combine(_secretsRoot, "acme", "github-pat")).Should().Be(Convert.ToHexStringLower(Encoding.UTF8.GetBytes(TestKeys.TestPat)) + "\n");
        SecretSweep.AssertAbsent([run.Stdout, run.Stderr]);
    }

    [Fact]
    public async Task SecretSet_ValueAsArgument_ExitTwo()
    {
        // Act
        ZyggyRun run = await Run(["secret", "set", "hmac/1", "somevalue", "--config", _nodeJson], null);

        // Assert
        run.ExitCode.Should().Be(2);
        Directory.Exists(_secretsRoot).Should().BeFalse();
    }

    [Fact]
    public async Task SecretSet_InvalidHex_ExitTwo()
    {
        // Act
        ZyggyRun run = await Run(["secret", "set", "hmac/1", "--hex", "--config", _nodeJson], "not-hex\n");

        // Assert
        run.ExitCode.Should().Be(2);
        run.Stderr.Should().Contain("not hexadecimal");
        Directory.Exists(_secretsRoot).Should().BeFalse();
    }

    [Fact]
    public async Task SecretSet_EmptyStdin_ExitTwo()
    {
        // Act
        ZyggyRun run = await Run(["secret", "set", "hmac/1", "--config", _nodeJson], "\n");

        // Assert
        run.ExitCode.Should().Be(2);
    }

    [Fact]
    public async Task SecretSet_NoNodeJson_ExitThree()
    {
        // Act
        ZyggyRun run = await Run(["secret", "set", "hmac/1", "--config", _scratch.File("absent.json")], "x\n");

        // Assert
        run.ExitCode.Should().Be(3);
        run.Stderr.Should().StartWith("zyggy: configuration error:");
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "Unix file modes: Linux only")]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task SecretSet_File_Is0600()
    {
        // Act
        await Run(["secret", "set", "hmac/1", "--hex", "--config", _nodeJson], Convert.ToHexStringLower(TestKeys.Secret1) + "\n");

        // Assert
        File.GetUnixFileMode(Path.Combine(_secretsRoot, "acme", "hmac", "1")).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact]
    public async Task SecretSet_Generate_32ByteKeyFileFingerprintLineOnly()
    {
        // Act: spec 09 AC-11 — a new key generated on this machine
        ZyggyRun run = await Run(["secret", "set", "hmac/2", "--generate", "--config", _nodeJson], null);

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        string file = File.ReadAllText(Path.Combine(_secretsRoot, "acme", "hmac", "2"));
        file.Should().MatchRegex("^[0-9a-f]{64}\n$");
        run.Stdout.Should().MatchRegex(@"^stored acme/hmac/2 \(32 bytes\) fingerprint [0-9a-f]{8}\r?\n$");
        run.Stdout.Should().NotContain(file.Trim());
        ExpectOnlyTheFileStoreWarning(run.Stderr);
        SecretSweep.AssertAbsent([run.Stdout, run.Stderr]);
        if (OperatingSystem.IsLinux())
        {
            File.GetUnixFileMode(Path.Combine(_secretsRoot, "acme", "hmac", "2")).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public async Task SecretSet_GenerateShow_HexLineEqualsTheFile()
    {
        // Act
        ZyggyRun run = await Run(["secret", "set", "hmac/2", "--generate", "--show", "--config", _nodeJson], null);

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        string hex = File.ReadAllText(Path.Combine(_secretsRoot, "acme", "hmac", "2")).Trim();
        string[] lines = run.Stdout.TrimEnd('\r', '\n').Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        lines.Should().HaveCount(2);
        lines[0].Should().MatchRegex(@"^stored acme/hmac/2 \(32 bytes\) fingerprint [0-9a-f]{8}$");
        lines[1].Should().Be("value (hex, shown once; carry it to the other machines of the tenant): " + hex);
        SecretSweep.AssertAbsent([lines[0], run.Stderr]);
    }

    [Fact]
    public async Task SecretSet_GenerateNonKeyName_ExitTwo()
    {
        // Act
        ZyggyRun run = await Run(["secret", "set", "github-pat", "--generate", "--config", _nodeJson], null);

        // Assert
        run.ExitCode.Should().Be(2);
        run.Stderr.Should().Contain("zyggy: --generate is for hmac/<n> keys");
        Directory.Exists(_secretsRoot).Should().BeFalse();
    }

    [Fact]
    public async Task SecretSet_Replace_NoteOnStderr()
    {
        // Arrange
        await Run(["secret", "set", "hmac/1", "--hex", "--config", _nodeJson], Convert.ToHexStringLower(TestKeys.Secret1) + "\n");

        // Act
        ZyggyRun run = await Run(["secret", "set", "hmac/1", "--hex", "--config", _nodeJson], Convert.ToHexStringLower(TestKeys.Secret2) + "\n");

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        run.Stderr.Should().Contain($"note: acme/hmac/1 replaced (previous fingerprint {SecretFingerprint.Of(TestKeys.Secret1)})");
        SecretSweep.AssertAbsent([run.Stdout, run.Stderr]);
    }

    [Fact(SkipUnless = nameof(IsWindows), Skip = "the file-store warning is for Windows only")]
    public async Task SecretSet_OnWindows_FileStoreWarningExactlyOncePerRun()
    {
        // Act
        ZyggyRun run = await Run(["secret", "set", "hmac/1", "--hex", "--config", _nodeJson], Convert.ToHexStringLower(TestKeys.Secret1) + "\n");

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        Regex.Count(run.Stderr, "file secret store selected on Windows: spike and test use only; the product store is Credential Manager").Should().Be(1, run.Stderr);
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "no file-store warning on Linux")]
    public async Task SecretSet_OnLinux_NoWarning()
    {
        // Act
        ZyggyRun run = await Run(["secret", "set", "hmac/1", "--hex", "--config", _nodeJson], Convert.ToHexStringLower(TestKeys.Secret1) + "\n");

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        run.Stderr.Should().NotContain("file secret store selected");
    }

    [Fact]
    public async Task SecretSet_UnknownStoreWord_ExitThree()
    {
        // Arrange: spec 09 AC-4 — the reader refuses before any store exists
        string nodeJson = NodeJson.Write(_scratch.File("cfg-vault"), Acme, MachineName.Parse("home-laptop"), KeyId.Parse("acme/1"), _scratch.File("bus"), "https://example.invalid/acme-org/acme-bus.git", _secretsRoot, secretsStore: "vault");

        // Act
        ZyggyRun run = await Run(["secret", "set", "hmac/1", "--hex", "--config", nodeJson], Convert.ToHexStringLower(TestKeys.Secret1) + "\n");

        // Assert
        run.ExitCode.Should().Be(3, run.Stderr);
        run.Stderr.Should().Contain("zyggy: configuration error: secrets.store: one of os, file");
    }

    // spec 09 AC-3: `secrets.store: file` on Windows warns once (spike and test use); on Linux the file layer is the product store's own
    private static void ExpectOnlyTheFileStoreWarning(string stderr)
    {
        if (OperatingSystem.IsWindows())
        {
            Regex.Count(stderr, "file secret store selected on Windows").Should().Be(1, stderr);
            // the console logger's blocks: the warning, and the verb's one Information line (fingerprint only, never a value)
            stderr.Split('\n').Where(l => l.Trim().Length > 0 && !l.StartsWith("warn: ", StringComparison.Ordinal) && !l.StartsWith("info: ", StringComparison.Ordinal) && !l.StartsWith("      ", StringComparison.Ordinal)).Should().BeEmpty(stderr);
        }
        else
        {
            stderr.Split('\n').Where(l => l.Trim().Length > 0 && !l.StartsWith("info: ", StringComparison.Ordinal) && !l.StartsWith("      ", StringComparison.Ordinal)).Should().BeEmpty(stderr);
        }
    }

    private Task<ZyggyRun> Run(string[] args, string? stdin) =>
        ZyggyCli.RunAsync(args, new Dictionary<string, string?>(), stdin, _scratch.Path, TestContext.Current.CancellationToken);
}
