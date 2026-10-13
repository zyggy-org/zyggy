using System.Text;

using Zyggy.Core.Envelope;
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
        run.Stdout.Should().Be("stored acme/hmac/1 (32 bytes)" + Environment.NewLine);
        run.Stderr.Should().BeEmpty();
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
        run.Stdout.Should().Be($"stored acme/github-pat ({TestKeys.TestPat.Length} bytes)" + Environment.NewLine);
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

    private Task<ZyggyRun> Run(string[] args, string? stdin) =>
        ZyggyCli.RunAsync(args, new Dictionary<string, string?>(), stdin, _scratch.Path, TestContext.Current.CancellationToken);
}
