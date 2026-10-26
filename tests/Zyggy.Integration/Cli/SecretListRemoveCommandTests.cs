using System.Text;

using Zyggy.Core.Envelope;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Cli;

/// <summary>Spec 09 AC-11 through the built binary over <c>secrets.store: file</c>: <c>secret list</c> and <c>secret remove</c>.</summary>
public sealed class SecretListRemoveCommandTests : IDisposable
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private readonly ScratchDirectory _scratch = new();
    private readonly string _nodeJson;
    private readonly string _secretsRoot;

    public SecretListRemoveCommandTests()
    {
        _secretsRoot = _scratch.File("secrets");
        _nodeJson = NodeJson.Write(_scratch.File("cfg"), Acme, MachineName.Parse("home-laptop"), KeyId.Parse("acme/1"), _scratch.File("bus"), "https://example.invalid/acme-org/acme-bus.git", _secretsRoot);
    }

    public void Dispose() => _scratch.Dispose();

    [Fact]
    public async Task SecretList_TwoKeysAndPat_TableWithRolesAndStoreFile()
    {
        // Arrange: seeded through the binary
        await SeedAsync();

        // Act
        ZyggyRun run = await Run(["secret", "list", "--config", _nodeJson], null);

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        string pat = SecretFingerprint.Of(Encoding.UTF8.GetBytes(TestKeys.TestPat));
        string one = SecretFingerprint.Of(TestKeys.Secret1);
        string two = SecretFingerprint.Of(TestKeys.Secret2);
        Lines(run.Stdout).Should().Equal(
            "name        bytes  fingerprint  store  role",
            $"github-pat  19     {pat}     file   ",
            $"hmac/1      32     {one}     file   signs",
            $"hmac/2      32     {two}     file   verify-only");
        SecretSweep.AssertAbsent([run.Stdout, run.Stderr]);
    }

    [Fact]
    public async Task SecretList_Empty_None()
    {
        // Act
        ZyggyRun run = await Run(["secret", "list", "--config", _nodeJson], null);

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        Lines(run.Stdout).Should().Equal("(none)");
    }

    [Fact]
    public async Task SecretRemove_Hmac2_FileDeletedExitZero()
    {
        // Arrange
        await SeedAsync();

        // Act
        ZyggyRun run = await Run(["secret", "remove", "hmac/2", "--config", _nodeJson], null);

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        Lines(run.Stdout).Should().Equal("removed acme/hmac/2");
        File.Exists(Path.Combine(_secretsRoot, "acme", "hmac", "2")).Should().BeFalse();
        File.Exists(Path.Combine(_secretsRoot, "acme", "hmac", "1")).Should().BeTrue();
    }

    [Fact]
    public async Task SecretRemove_Absent_ExitOne()
    {
        // Act
        ZyggyRun run = await Run(["secret", "remove", "hmac/9", "--config", _nodeJson], null);

        // Assert
        run.ExitCode.Should().Be(1);
        run.Stderr.Should().Contain("zyggy: acme/hmac/9 is not in the file");
    }

    [Fact]
    public async Task SecretRemove_SignWithKey_ExitTwoFileKept()
    {
        // Arrange
        await SeedAsync();

        // Act
        ZyggyRun run = await Run(["secret", "remove", "hmac/1", "--config", _nodeJson], null);

        // Assert
        run.ExitCode.Should().Be(2);
        run.Stderr.Should().Contain("zyggy: acme/hmac/1 is this machine's signing key (signWith); change signWith first");
        File.Exists(Path.Combine(_secretsRoot, "acme", "hmac", "1")).Should().BeTrue();
    }

    [Fact]
    public async Task SecretRemove_NoNodeJson_ExitThree()
    {
        // Act
        ZyggyRun run = await Run(["secret", "remove", "hmac/1", "--config", _scratch.File("absent.json")], null);

        // Assert
        run.ExitCode.Should().Be(3);
        run.Stderr.Should().StartWith("zyggy: configuration error:");
    }

    private async Task SeedAsync()
    {
        (await Run(["secret", "set", "hmac/1", "--hex", "--config", _nodeJson], Convert.ToHexStringLower(TestKeys.Secret1) + "\n")).ExitCode.Should().Be(0);
        (await Run(["secret", "set", "hmac/2", "--hex", "--config", _nodeJson], Convert.ToHexStringLower(TestKeys.Secret2) + "\n")).ExitCode.Should().Be(0);
        (await Run(["secret", "set", "github-pat", "--config", _nodeJson], TestKeys.TestPat + "\n")).ExitCode.Should().Be(0);
    }

    private static string[] Lines(string text) => text.TrimEnd('\r', '\n').Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

    private Task<ZyggyRun> Run(string[] args, string? stdin) =>
        ZyggyCli.RunAsync(args, new Dictionary<string, string?>(), stdin, _scratch.Path, TestContext.Current.CancellationToken);
}
