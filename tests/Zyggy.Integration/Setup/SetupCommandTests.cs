using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Zyggy.Core.Secrets;
using Zyggy.Core.Setup;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Setup;

/// <summary>
/// Spec 42 AC-7..AC-9, AC-19, AC-20, AC-22 and the CLI surface (plan 42 Step 2): <c>zyggy setup</c> in process on a temp profile with a file
/// secret store — the <c>node.json</c> and secret lines on stdout in order, the verb's exit code, no secret value anywhere.
/// </summary>
public sealed partial class SetupCommandTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string PatFingerprint => SecretFingerprint.Of(Encoding.UTF8.GetBytes(TestKeys.TestPat));

    [Fact]
    public async Task Setup_OldSpikeNodeJsonWithoutClaudeOrSignWith_SignWithGiven_CompletedWithBackup_SecretLinesPresent_ExitZeroOrSix()
    {
        // Arrange
        await using SetupHarness harness = await SetupHarness.CreateAsync(Ct);
        string config = harness.WriteNodeJson(harness.OldSpikeJson());
        byte[] original = File.ReadAllBytes(config);
        string head = await harness.Core.HeadAsync(Ct);

        // Act
        (int exit, VerbConsole console) = await harness.RunAsync(harness.Args(signWith: "acme/1"), Ct);

        // Assert: exit 0 while settings and skills are not built yet (revisited in Step 6)
        exit.Should().Be(SetupExit.Ok, console.Stdout + console.Stderr);
        string backup = Directory.GetFiles(Path.GetDirectoryName(config)!, "node.json.bak-*").Should().ContainSingle().Subject;
        BackupSuffix().IsMatch(backup).Should().BeTrue(backup);
        File.ReadAllBytes(backup).Should().Equal(original);
        string[] lines = console.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().Equal(
            $"setup: node.json: done - added claude.path; added signWith (backup {backup})",
            $"setup: zyggy-core: done - cloned {harness.CoreRemote} at {head[..12]}",
            $"setup: signing key acme/1: already done - present (fingerprint {SecretFingerprint.Of(TestKeys.Secret1)})",
            $"setup: github-pat: already done - present (fingerprint {PatFingerprint})");
        JsonNode written = JsonNode.Parse(File.ReadAllText(config))!;
        written["claude"]!["path"]!.GetValue<string>().Should().Be(Path.GetFullPath(harness.ClaudeExe).Replace('\\', '/'));
        written["signWith"]!.GetValue<string>().Should().Be("acme/1");
        harness.Hooks.Calls.Select(HookedProcessRunner.SubCommand).Should().Equal("clone", "rev-parse");
    }

    [Fact]
    public async Task Setup_SignWithAbsentAndNotGiven_ExitThree_FileUntouched()
    {
        // Arrange
        await using SetupHarness harness = await SetupHarness.CreateAsync(Ct);
        string config = harness.WriteNodeJson(harness.OldSpikeJson());
        byte[] original = File.ReadAllBytes(config);

        // Act
        (int exit, VerbConsole console) = await harness.RunAsync(harness.Args(), Ct);

        // Assert
        exit.Should().Be(SetupExit.NodeConfig);
        console.Stdout.Should().Be("setup: node.json: failed - signWith missing; run install.ps1 again with -SignWith acme/<n>\n");
        File.ReadAllBytes(config).Should().Equal(original);
        Directory.GetFiles(Path.GetDirectoryName(config)!).Should().Equal(config);
    }

    [Fact]
    public async Task Setup_TenantDiffers_ExitThree_BothValuesNamed()
    {
        // Arrange
        await using SetupHarness harness = await SetupHarness.CreateAsync(Ct);
        string config = harness.WriteNodeJson(harness.OldSpikeJson(signWith: "acme/1"));
        byte[] original = File.ReadAllBytes(config);

        // Act
        (int exit, VerbConsole console) = await harness.RunAsync(harness.Args(tenant: "globex", signWith: "globex/1"), Ct);

        // Assert
        exit.Should().Be(SetupExit.NodeConfig);
        console.Stdout.Should().Be("setup: node.json: failed - node.json names tenant acme and machine home-laptop; the installer was given globex and home-laptop\n");
        File.ReadAllBytes(config).Should().Equal(original);
    }

    [Fact]
    public async Task Setup_MachineCentral_ExitTwo_OneLine()
    {
        // Arrange
        await using SetupHarness harness = await SetupHarness.CreateAsync(Ct);
        string config = harness.WriteNodeJson(harness.OldSpikeJson(signWith: "acme/1", machine: "central"));
        byte[] original = File.ReadAllBytes(config);

        // Act
        (int exit, VerbConsole console) = await harness.RunAsync(harness.Args(machine: "central"), Ct);

        // Assert
        exit.Should().Be(SetupExit.Usage);
        console.Stdout.Should().Be("setup: node.json: failed - setup is for laptops; Central is installed by install-node.md\n");
        File.ReadAllBytes(config).Should().Equal(original);
    }

    [Fact]
    public async Task Setup_SigningKeyMissingFromStore_ExitFour_LineNamesSecretSetHmac1Hex()
    {
        // Arrange
        await using SetupHarness harness = await SetupHarness.CreateAsync(Ct, signingKey: false);
        harness.WriteNodeJson(harness.OldSpikeJson(signWith: "acme/1"));

        // Act
        (int exit, VerbConsole console) = await harness.RunAsync(harness.Args(), Ct);

        // Assert
        exit.Should().Be(SetupExit.SigningKeyMissing);
        console.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(2).Should().Equal(
            "setup: signing key acme/1: missing - run: zyggy secret set hmac/1 --hex",
            $"setup: github-pat: already done - present (fingerprint {PatFingerprint})");
    }

    [Fact]
    public async Task Setup_MissingTenantOption_UsageExitTwo()
    {
        // Arrange
        await using SetupHarness harness = await SetupHarness.CreateAsync(Ct);
        string config = harness.WriteNodeJson(harness.OldSpikeJson(signWith: "acme/1"));

        // Act
        (int exit, VerbConsole console) = await harness.RunAsync(["setup", "--config", config, "--machine", "home-laptop"], Ct);

        // Assert
        exit.Should().Be(SetupExit.Usage);
        console.Stderr.Should().Contain("--tenant");
        console.Stdout.Should().BeEmpty();
    }

    [Fact]
    public async Task Setup_MalformedSignWith_UsageExitTwo_NothingTouched()
    {
        // Arrange
        await using SetupHarness harness = await SetupHarness.CreateAsync(Ct);
        string config = harness.WriteNodeJson(harness.OldSpikeJson());
        byte[] original = File.ReadAllBytes(config);

        // Act
        (int exit, VerbConsole console) = await harness.RunAsync(harness.Args(signWith: "acme-1"), Ct);

        // Assert
        exit.Should().Be(SetupExit.Usage);
        console.Stderr.Should().Be("zyggy: --sign-with: must be <tenant>/<n>\n");
        File.ReadAllBytes(config).Should().Equal(original);
    }

    [Fact]
    public async Task Setup_NoSecretValueAnywhere_Sweep()
    {
        // Arrange
        await using SetupHarness harness = await SetupHarness.CreateAsync(Ct);
        harness.WriteNodeJson(harness.OldSpikeJson());

        // Act
        (int exit, VerbConsole console) = await harness.RunAsync(harness.Args(signWith: "acme/1"), Ct);

        // Assert
        exit.Should().Be(SetupExit.Ok, console.Stdout + console.Stderr);
        harness.Sweep(console);
    }

    [Fact]
    public async Task Setup_Help_ListsTheSevenOptions()
    {
        // Arrange
        await using SetupHarness harness = await SetupHarness.CreateAsync(Ct);

        // Act
        (int exit, VerbConsole console) = await harness.RunAsync(["setup", "--help"], Ct);

        // Assert
        exit.Should().Be(0);
        console.Stdout.Should().ContainAll("--config", "--tenant", "--machine", "--sign-with", "--core-path", "--claude-dir", "--core-remote", "for tests and forks");
    }

    [GeneratedRegex(@"\.bak-\d{8}T\d{6}Z$")]
    private static partial Regex BackupSuffix();
}
