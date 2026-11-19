using System.Text.Json.Nodes;

using Zyggy.Core.Configuration;

namespace Zyggy.Core.Tests.Configuration;

// AC-5: a change rewrites only the named key, keeps a backup, and an identical second call prints unchanged and writes nothing.
// AC-6: a file the reader refuses (a key named like a secret) is never replaced; exit 3. Spec 55 slice 3.
public sealed class SettingsVerbWriteTests : IDisposable
{
    private readonly SettingsTree _tree = new();

    public SettingsVerbWriteTests() => _tree.WithMemory();

    public void Dispose() => _tree.Dispose();

    private string EverySection => $$"""
        "claude": { "path": "C:/tools/claude.exe", "maxTurns": 40, "defaultAllowedTools": ["Read", "Grep"] },
          "poll": { "activeSeconds": 15, "idleSeconds": 120, "safetyFetchMinutes": 30 },
          "secrets": { "store": "file", "root": "C:/secrets" },
          "workHours": { "timezone": "Europe/Brussels", "days": ["Mon", "Tue"], "from": "08:00", "to": "18:00" },
          "maxConcurrentJobs": 3,
          "log": { "level": "Debug" },
          "archive": { "itemMaxBytes": 1048576 },
          "sweep": { "intervalMinutes": 30 },
          "agentCore": { "path": "C:/agents/zyggy-core" },
          "nodeDir": "C:/zyggy",
          "role": "central",
          "devRoots": ["{{SettingsTree.J(_tree.Parent)}}"],
          "discovery": { "intervalMinutes": 30, "depth": 3, "detail": "full", "exclude": ["home"], "naming": "leaf" },
          "supervision": { "enabled": true, "statusMinutes": 5, "project": "instance", "runsPerHour": 6, "outboundPaths": ["{{SettingsTree.J(_tree.Business)}}"], "workMachines": ["work-laptop"] }
        """;

    [Fact]
    public async Task Add_DevRoots_EverySectionPresent_OnlyDevRootsChanged()
    {
        // Arrange
        _tree.Write(extra: EverySection);
        JsonObject before = JsonNode.Parse(File.ReadAllText(_tree.Config))!.AsObject();

        // Act
        var (exit, _, _) = await _tree.RunAsync("add", "devRoots", _tree.Other);

        // Assert
        exit.Should().Be(SettingsVerb.Ok);
        JsonObject after = JsonNode.Parse(File.ReadAllText(_tree.Config))!.AsObject();
        after["devRoots"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal(_tree.Parent, _tree.Other);
        before.Remove("devRoots");
        after.Remove("devRoots");
        JsonNode.DeepEquals(after, before).Should().BeTrue();
    }

    [Fact]
    public async Task Add_DevRoots_EverySectionPresent_KeyOrderKept()
    {
        // Arrange
        _tree.Write(extra: EverySection);
        string[] order = [.. JsonNode.Parse(File.ReadAllText(_tree.Config))!.AsObject().Select(p => p.Key)];

        // Act
        await _tree.RunAsync("add", "devRoots", _tree.Other);

        // Assert
        JsonNode.Parse(File.ReadAllText(_tree.Config))!.AsObject().Select(p => p.Key).Should().Equal(order);
    }

    [Fact]
    public async Task Add_Exclude_EverySectionPresent_OnlyTheExcludeListChanged()
    {
        // Arrange
        _tree.Write(extra: EverySection);
        JsonObject before = JsonNode.Parse(File.ReadAllText(_tree.Config))!.AsObject();

        // Act
        var (exit, output, _) = await _tree.RunAsync("add", "discovery.exclude", "tmp*");

        // Assert
        exit.Should().Be(SettingsVerb.Ok);
        output.Should().StartWith("settings: discovery.exclude: home -> home, tmp*\n");
        JsonObject after = JsonNode.Parse(File.ReadAllText(_tree.Config))!.AsObject();
        after["discovery"]!["exclude"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("home", "tmp*");
        before["discovery"]!.AsObject().Remove("exclude");
        after["discovery"]!.AsObject().Remove("exclude");
        JsonNode.DeepEquals(after, before).Should().BeTrue();
    }

    [Fact]
    public async Task Change_Written_BackupHoldsTheOriginalBytes()
    {
        // Arrange
        _tree.Write(extra: EverySection);
        byte[] original = File.ReadAllBytes(_tree.Config);

        // Act
        await _tree.RunAsync("add", "devRoots", _tree.Other);

        // Assert
        File.ReadAllBytes(_tree.BackupPath).Should().Equal(original);
    }

    [Fact]
    public async Task Change_SecondIdenticalCall_PrintsUnchangedAndWritesNothing()
    {
        // Arrange
        _tree.Write(extra: EverySection);
        await _tree.RunAsync("add", "devRoots", _tree.Other);
        byte[] written = File.ReadAllBytes(_tree.Config);
        _tree.Clock.Advance(TimeSpan.FromMinutes(5));

        // Act
        var (exit, output, _) = await _tree.RunAsync("add", "devRoots", _tree.Other);

        // Assert
        exit.Should().Be(SettingsVerb.Ok);
        output.Should().Be("settings: devRoots: unchanged\n");
        File.ReadAllBytes(_tree.Config).Should().Equal(written);
        Directory.GetFiles(_tree.Root, "node.json.bak-*").Should().ContainSingle();
    }

    [Fact]
    public async Task Add_DevRootsWithExcludes_OneWriteWithBothKeys()
    {
        // Arrange
        _tree.Write();

        // Act
        var (exit, output, _) = await _tree.RunAsync("add", "devRoots", _tree.Parent, "--exclude", "home", "--exclude", "tmp*");

        // Assert
        exit.Should().Be(SettingsVerb.Ok);
        output.Should().Be(
            $"settings: devRoots: (none) -> {_tree.Parent}\n"
            + "settings: discovery.exclude: (none) -> home, tmp*\n"
            + "settings: publishes: instance\n"
            + "settings: the node applies this within a minute\n");
        Directory.GetFiles(_tree.Root, "node.json.bak-*").Should().ContainSingle();
    }

    [Fact]
    public async Task Change_FileWithASecretNamedKey_Exit3FileUntouchedNoBackup()
    {
        // Arrange
        _tree.Write(extra: "\"githubToken\": \"abc\"");
        byte[] original = File.ReadAllBytes(_tree.Config);

        // Act
        var (exit, _, error) = await _tree.RunAsync("add", "devRoots", _tree.Other);

        // Assert
        exit.Should().Be(SettingsVerb.Configuration);
        error.Should().Contain("githubToken");
        File.ReadAllBytes(_tree.Config).Should().Equal(original);
        File.Exists(_tree.BackupPath).Should().BeFalse();
    }

    [Fact]
    public async Task Change_ResultTheReaderRefuses_Exit3FileUntouchedNoTempNoBackup()
    {
        // Arrange: the reader accepts the original; the guard sees the written result refused (a key named like a secret slipped in).
        _tree.WriteCentralShaped();
        byte[] original = File.ReadAllBytes(_tree.Config);
        var verb = new SettingsVerb(_tree.Environment, _tree.Clock, (_, _) => Task.FromResult<IReadOnlyList<SettingsProject>>([new SettingsProject("instance", _tree.Instance), new SettingsProject("calizr", Path.Combine(_tree.Other, "calizr"))]))
        {
            BeforeWrite = root => root["apiToken"] = "x",
        };
        using var output = new StringWriter();
        using var error = new StringWriter();

        // Act
        int exit = await verb.RunAsync(["add", "devRoots", _tree.Other, "--config", _tree.Config], new Verbs.VerbIo(TextReader.Null, output, error, false), CancellationToken.None);

        // Assert
        exit.Should().Be(SettingsVerb.Configuration);
        File.ReadAllBytes(_tree.Config).Should().Equal(original);
        Directory.GetFiles(_tree.Root, "node.json.bak-*").Concat(Directory.GetFiles(_tree.Root, "node.json.tmp-*")).Should().BeEmpty();
    }

    [Fact]
    public async Task Show_PrintsTheFiveKeys()
    {
        // Arrange
        _tree.WriteCentralShaped();
        _tree.Environment["ZYGGY_HOOKS"] = "off";

        // Act
        var (exit, output, _) = await _tree.RunAsync("show");

        // Assert
        exit.Should().Be(SettingsVerb.Ok);
        output.Should().Be(
            $"devRoots: {_tree.Parent}\n"
            + "discovery.exclude: home\n"
            + "supervision.project: instance\n"
            + "supervision.workMachines: (none)\n"
            + "supervision.outboundPaths: (none)\n");
    }

    [Fact]
    public async Task Show_FileTheReaderRefuses_Exit3()
    {
        // Arrange
        _tree.Write(extra: "\"githubToken\": \"abc\"");

        // Act
        var (exit, _, _) = await _tree.RunAsync("show");

        // Assert
        exit.Should().Be(SettingsVerb.Configuration);
    }
}
