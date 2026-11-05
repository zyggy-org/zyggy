using System.Text.Json.Nodes;

using Zyggy.Core.Secrets;
using Zyggy.Core.Setup;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Integration.Setup;

/// <summary>
/// Spec 42 AC-13..AC-17, AC-19, AC-21, AC-22 (plan 42 Step 6): the whole <c>zyggy setup</c> in process on a temp profile with a file secret
/// store, real git against the local bare <c>zyggy-core</c> stand-in, and every process recorded — the first run wires everything, the second
/// changes nothing. Never the real profile, the real <c>claude</c>, GitHub or the Credential Manager.
/// </summary>
public sealed class SetupEndToEndTests
{
    private const string OwnerSettings = """
        {
          "model": "opus",
          "env": { "OTHER": "1" },
          "permissions": {
            "allow": [ "Bash(git status)", "Read(*)" ],
            "deny": [ "Bash(zyggy secret*)", "Bash(rm -rf*)" ]
          },
          "hooks": {
            "Stop": [ { "hooks": [ { "type": "command", "command": "zyggy context --stop-hook", "timeout": 30 } ] } ],
            "SessionEnd": [ { "hooks": [ { "type": "command", "command": "zyggy context --flush" } ] } ]
          }
        }
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Setup_FirstRun_SpikeNodeJson_AllEightLinesInOrder_ExitZero_FilesWired()
    {
        // Arrange
        await using SetupHarness harness = await ReadyAsync();
        string head = await harness.Core.HeadAsync(Ct);

        // Act
        (int exit, VerbConsole console) = await harness.RunAsync(harness.Args(signWith: "acme/1"), Ct);

        // Assert
        exit.Should().Be(SetupExit.Ok, console.Stdout + console.Stderr);
        string backup = Directory.GetFiles(Path.GetDirectoryName(harness.ConfigPath)!, "node.json.bak-*").Should().ContainSingle().Subject;
        Lines(console).Should().Equal(harness.FirstRunLines($"added claude.path; added signWith (backup {backup})", head));
        var expected = new JsonObject
        {
            ["env"] = new JsonObject { ["ZYGGY_SECRET_PATTERNS"] = harness.Patterns },
            ["permissions"] = new JsonObject { ["deny"] = new JsonArray("Bash(zyggy secret*)", "Bash(zyggy context --stop-hook*)", "Bash(zyggy context --flush*)") },
        };
        JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(harness.SettingsFile)), expected).Should().BeTrue();
        File.Exists(harness.Patterns).Should().BeTrue();
        File.ReadAllBytes(Path.Combine(SetupPaths.SkillTarget(harness.ClaudeDir, "remember"), "SKILL.md"))
            .Should().Equal(File.ReadAllBytes(Path.Combine(harness.Core.SeedDir, "node", "skills", "remember", "SKILL.md")));
        File.ReadAllBytes(Path.Combine(SetupPaths.SkillTarget(harness.ClaudeDir, "archive"), "SKILL.md"))
            .Should().Equal(File.ReadAllBytes(Path.Combine(harness.Core.SeedDir, "node", "skills", "archive", "SKILL.md")));
    }

    [Fact]
    public async Task Setup_SecondRun_OnlyAlreadyDoneLines_ExitZero_NoBackup_SnapshotUnchanged()
    {
        // Arrange
        await using SetupHarness harness = await ReadyAsync();
        await harness.RunAsync(harness.Args(signWith: "acme/1"), Ct);
        IReadOnlyDictionary<string, (string Sha256, DateTime LastWriteUtc)> before = harness.Snapshot();

        // Act
        (int exit, VerbConsole console) = await harness.RunAsync(harness.Args(signWith: "acme/1"), Ct);

        // Assert
        exit.Should().Be(SetupExit.Ok, console.Stdout);
        Lines(console).Should().HaveCount(8).And.OnlyContain(l => l.Contains(": already done", StringComparison.Ordinal));
        harness.Snapshot().Should().Equal(before);
        Directory.Exists(Path.Combine(harness.LocalAppData, "zyggy", "backup")).Should().BeFalse();
        Directory.GetFiles(harness.ClaudeDir, "*.bak-*").Should().BeEmpty();
    }

    [Fact]
    public async Task Setup_ExistingSettingsWithOwnerHooks_MergedAdditively_HooksByteIdentical_BackupBeside()
    {
        // Arrange
        await using SetupHarness harness = await ReadyAsync();
        harness.SeedSettings(OwnerSettings);
        JsonObject before = JsonNode.Parse(OwnerSettings)!.AsObject();

        // Act
        (int exit, VerbConsole console) = await harness.RunAsync(harness.Args(signWith: "acme/1"), Ct);

        // Assert
        exit.Should().Be(SetupExit.Ok, console.Stdout);
        string backup = Directory.GetFiles(harness.ClaudeDir, "settings.json.bak-*").Should().ContainSingle().Subject;
        File.ReadAllText(backup).Should().Be(OwnerSettings);
        JsonObject after = JsonNode.Parse(File.ReadAllText(harness.SettingsFile))!.AsObject();
        after.Select(p => p.Key).Should().Equal("model", "env", "permissions", "hooks");
        after["hooks"]!.ToJsonString().Should().Be(before["hooks"]!.ToJsonString());
        JsonNode.DeepEquals(after["permissions"]!["allow"], before["permissions"]!["allow"]).Should().BeTrue();
        after["permissions"]!["deny"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal(
            "Bash(zyggy secret*)", "Bash(rm -rf*)", "Bash(zyggy context --stop-hook*)", "Bash(zyggy context --flush*)");
        after["env"]!.AsObject().Select(p => p.Key).Should().Equal("OTHER", "ZYGGY_SECRET_PATTERNS");
        Lines(console).Where(l => l.StartsWith("setup: settings: ", StringComparison.Ordinal)).Should().Equal(
            $"setup: settings: done - env.ZYGGY_SECRET_PATTERNS set to {harness.Patterns} (backup {backup})",
            $"setup: settings: done - permissions.deny gained Bash(zyggy context --stop-hook*), Bash(zyggy context --flush*) (backup {backup})");
    }

    [Fact]
    public async Task Setup_GithubPatMissing_ExitSix_TaskWouldStillStartLineNamesSecretSetGithubPat()
    {
        // Arrange
        await using SetupHarness harness = await SetupHarness.CreateAsync(Ct, pat: false);
        harness.WriteNodeJson(harness.OldSpikeJson(signWith: "acme/1"));

        // Act
        (int exit, VerbConsole console) = await harness.RunAsync(harness.Args(), Ct);

        // Assert
        exit.Should().Be(SetupExit.Incomplete);
        Lines(console).Last().Should().Be("setup: github-pat: missing - run: zyggy secret set github-pat");
        Lines(console).Take(6).Should().OnlyContain(l => l.Contains(": done", StringComparison.Ordinal) || l.Contains(": already done", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Setup_SettingsNotValidJson_ExitSix_FileUntouched_OtherItemsDone()
    {
        // Arrange
        await using SetupHarness harness = await ReadyAsync();
        harness.SeedSettings("{ \"model\": \"opus\", ");
        byte[] original = File.ReadAllBytes(harness.SettingsFile);
        DateTime written = File.GetLastWriteTimeUtc(harness.SettingsFile);

        // Act
        (int exit, VerbConsole console) = await harness.RunAsync(harness.Args(signWith: "acme/1"), Ct);

        // Assert
        exit.Should().Be(SetupExit.Incomplete);
        File.ReadAllBytes(harness.SettingsFile).Should().Equal(original);
        File.GetLastWriteTimeUtc(harness.SettingsFile).Should().Be(written);
        Directory.GetFiles(harness.ClaudeDir).Should().Equal(harness.SettingsFile);
        Lines(console).Select(l => l.Split(" - ")[0]).Should().Equal(
            "setup: node.json: done",
            "setup: zyggy-core: done",
            "setup: settings: failed",
            "setup: skill remember: done",
            "setup: skill archive: done",
            "setup: signing key acme/1: already done",
            "setup: github-pat: already done");
        Lines(console)[2].Should().Be("setup: settings: failed - not valid JSON; left untouched");
    }

    [Fact]
    public async Task Setup_ClaudeConfigDirSet_SettingsAndSkillsGoThere()
    {
        // Arrange
        await using SetupHarness harness = await ReadyAsync();
        string variableDir = harness.Scratch.File("variable-claude");

        // Act
        (int exit, VerbConsole console) = await harness.RunAsync(
            harness.Args(signWith: "acme/1"),
            new Dictionary<string, string?> { [SetupPaths.ClaudeConfigDirVariable] = variableDir },
            more: null,
            Ct);

        // Assert
        exit.Should().Be(SetupExit.Ok, console.Stdout);
        File.Exists(SetupPaths.SettingsFile(variableDir)).Should().BeTrue();
        File.Exists(Path.Combine(SetupPaths.SkillTarget(variableDir, "remember"), "SKILL.md")).Should().BeTrue();
        File.Exists(Path.Combine(SetupPaths.SkillTarget(variableDir, "archive"), "SKILL.md")).Should().BeTrue();
        Directory.Exists(harness.ClaudeDir).Should().BeFalse();
    }

    [Fact]
    public async Task Setup_ClaudeDirOption_WinsOverVariable()
    {
        // Arrange
        await using SetupHarness harness = await ReadyAsync();
        string variableDir = harness.Scratch.File("variable-claude");
        string optionDir = harness.Scratch.File("option-claude");

        // Act
        (int exit, VerbConsole console) = await harness.RunAsync(
            [.. harness.Args(signWith: "acme/1"), "--claude-dir", optionDir],
            new Dictionary<string, string?> { [SetupPaths.ClaudeConfigDirVariable] = variableDir },
            more: null,
            Ct);

        // Assert
        exit.Should().Be(SetupExit.Ok, console.Stdout);
        File.Exists(SetupPaths.SettingsFile(optionDir)).Should().BeTrue();
        File.Exists(Path.Combine(SetupPaths.SkillTarget(optionDir, "remember"), "SKILL.md")).Should().BeTrue();
        Directory.Exists(variableDir).Should().BeFalse();
        Directory.Exists(harness.ClaudeDir).Should().BeFalse();
    }

    [Fact]
    public async Task Setup_SkillDiffersFromTemplate_MovedToLocalAppDataBackup_Replaced()
    {
        // Arrange
        await using SetupHarness harness = await ReadyAsync();
        const string Own = "---\nname: remember\n---\nThe owner's own skill of that name.\n";
        string target = harness.SeedSkill("remember", Own);

        // Act
        (int exit, VerbConsole console) = await harness.RunAsync(harness.Args(signWith: "acme/1"), Ct);

        // Assert
        exit.Should().Be(SetupExit.Ok, console.Stdout);
        string stampDir = Directory.GetDirectories(Path.Combine(harness.LocalAppData, "zyggy", "backup")).Should().ContainSingle().Subject;
        string backup = Path.Combine(stampDir, "skills", "remember");
        File.ReadAllText(Path.Combine(backup, "SKILL.md")).Should().Be(Own);
        File.ReadAllBytes(Path.Combine(target, "SKILL.md"))
            .Should().Equal(File.ReadAllBytes(Path.Combine(harness.Core.SeedDir, "node", "skills", "remember", "SKILL.md")));
        Lines(console).Should().Contain($"setup: skill remember: done - copied from {SetupPaths.SkillSource(harness.CorePath, "remember")} (backup {backup})");
        Directory.GetDirectories(Path.Combine(harness.ClaudeDir, "skills")).Select(Path.GetFileName).Should().BeEquivalentTo("remember", "archive");
    }

    [Fact]
    public async Task Setup_SpikeFolderPresent_InfoLine_FolderUntouched()
    {
        // Arrange: secrets.store os (a file store over the same root stands in for the OS store), and an old spike folder
        await using SetupHarness harness = await SetupHarness.CreateAsync(Ct);
        harness.WriteNodeJson(harness.OldSpikeJson(signWith: "acme/1").Replace("\"store\": \"file\"", "\"store\": \"os\"", StringComparison.Ordinal));
        string spike = Path.Combine(harness.LocalAppData, "zyggy", "secrets", "acme");
        Directory.CreateDirectory(spike);
        string spikeFile = Path.Combine(spike, "hmac-1");
        File.WriteAllText(spikeFile, TestKeys.TestPat);
        DateTime written = File.GetLastWriteTimeUtc(spikeFile);

        // Act
        (int exit, VerbConsole console) = await harness.RunAsync(harness.Args(), extraEnvironment: null, more: s => s.AddFileSecretStore(o => o.RootDirectory = harness.SecretsRoot), Ct);

        // Assert
        exit.Should().Be(SetupExit.Ok, console.Stdout);
        Lines(console).Should().HaveCount(9);
        Lines(console).Last().Should().Be($"setup: spike file store: left as is - a spike file store exists at {spike}; its keys are retired (rotate-hmac.md step 0) and not imported");
        File.ReadAllText(spikeFile).Should().Be(TestKeys.TestPat);
        File.GetLastWriteTimeUtc(spikeFile).Should().Be(written);
        Directory.GetFiles(spike).Should().Equal(spikeFile);
        SecretSweep.AssertAbsent([console.Stdout, console.Stderr]);
    }

    [Fact]
    public async Task Setup_NoSecretValueAnywhere_Sweep_IncludingEveryWrittenFile()
    {
        // Arrange: a first run wires everything (backups included), a second one reads it all again
        await using SetupHarness harness = await ReadyAsync();
        harness.SeedSettings(OwnerSettings);
        harness.SeedSkill("archive", "---\nname: archive\n---\nAn older copy.\n");
        (_, VerbConsole first) = await harness.RunAsync(harness.Args(signWith: "acme/1"), Ct);

        // Act
        (int exit, VerbConsole second) = await harness.RunAsync(harness.Args(signWith: "acme/1"), Ct);

        // Assert
        exit.Should().Be(SetupExit.Ok, second.Stdout);
        harness.Sweep(first);
        harness.Sweep(second);
    }

    private static async Task<SetupHarness> ReadyAsync()
    {
        SetupHarness harness = await SetupHarness.CreateAsync(Ct);
        harness.WriteNodeJson(harness.OldSpikeJson());
        return harness;
    }

    private static string[] Lines(VerbConsole console) => console.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
}
