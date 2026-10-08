using Zyggy.Core.M365;
using Zyggy.Core.M365.Runs;
using Zyggy.Core.M365.Tools;

namespace Zyggy.Core.Tests.M365;

/// <summary>The model requests of the brief and the backfills: the run lists, caps and model of each kind, the prompt on stdin, a minimal environment.</summary>
public sealed class M365RunRequestTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zyggy-ut", Guid.NewGuid().ToString("N"));
    private readonly M365Environment _instance;
    private readonly M365Configuration _configuration;
    private readonly M365ToolPartition _partition;

    public M365RunRequestTests()
    {
        var tools = Path.Combine(_root, "checkout", ".claude", "skills", "m365", "tools");
        Directory.CreateDirectory(tools);
        foreach (var file in Directory.EnumerateFiles(M365Run.Golden("tools")))
        {
            File.Copy(file, Path.Combine(tools, Path.GetFileName(file)));
        }

        Directory.CreateDirectory(Path.Combine(_root, "checkout", "instance"));
        File.Copy(M365Run.Golden("fixtures", "m365.json"), Path.Combine(_root, "checkout", "instance", "m365.json"));
        _instance = M365Environment.Load(new Dictionary<string, string?> { ["ZYGGY_INSTANCE_DIR"] = Path.Combine(_root, "checkout", "instance"), ["HOME"] = _root }).Environment!;
        _configuration = M365Configuration.Load(_instance.ConfigPath, false, new DateOnly(2026, 9, 30), _ => true).Configuration!;
        _partition = M365ToolPartition.Load(_instance.Checkout).Partition!;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("Brief", "brief", 40, "3.0", null)]
    [InlineData("MailBackfill", "mail-backfill", 15, "0.5", "sonnet")]
    [InlineData("FilesBackfill", "files-backfill", 25, "0.5", "sonnet")]
    public void For_Kind_ListsTurnsBudgetModelFromConfig(string kind, string lists, int turns, string budget, string? model)
    {
        // Act
        var request = M365RunRequest.For(Enum.Parse<M365RunKind>(kind), "/x", _instance, _configuration, _partition, runDirectory: null);

        // Assert
        request.AllowedTools.Should().Equal(File.ReadAllLines(M365Run.Golden("run-lists", lists + "-allow.txt")));
        request.DisallowedTools.Where(t => !t.StartsWith("Read(//", StringComparison.Ordinal)).Should().Equal(File.ReadAllLines(M365Run.Golden("run-lists", lists + "-deny.txt")), "the brief adds its per-run memory deny after the list");
        request.MaxTurns.Should().Be(turns);
        request.MaxBudgetUsd.Should().Be(decimal.Parse(budget, System.Globalization.CultureInfo.InvariantCulture));
        request.Model.Should().Be(model);
    }

    [Fact]
    public void For_PromptOnStdinNeverInArguments()
    {
        // Act
        var request = M365RunRequest.For(M365RunKind.Brief, "/morning-brief alice@acme.example inbox", _instance, _configuration, _partition, null);

        // Assert
        request.Prompt.Should().Be("/morning-brief alice@acme.example inbox");
        Zyggy.Core.Models.ClaudeArguments.Build(request).Should().NotContain(a => a.Contains("/morning-brief", StringComparison.Ordinal));
    }

    [Fact]
    public void For_EnvironmentHooksOffAndRunDirOnly()
    {
        // Act
        var request = M365RunRequest.For(M365RunKind.Brief, "/x", _instance, _configuration, _partition, "/run/dir");

        // Assert
        request.Environment.Should().BeEquivalentTo(new Dictionary<string, string> { ["ZYGGY_HOOKS"] = "off", ["ZYGGY_M365_RUN_DIR"] = "/run/dir" });
        M365RunRequest.For(M365RunKind.MailBackfill, "/x", _instance, _configuration, _partition, null).Environment
            .Should().BeEquivalentTo(new Dictionary<string, string> { ["ZYGGY_HOOKS"] = "off" });
    }

    [Fact]
    public void For_CredentialsDirectorySet_PassedToTheRunForTheHeadersHelper()
    {
        // Arrange: the brief unit's LoadCredential directory
        var instance = M365Environment.Load(new Dictionary<string, string?>
        {
            ["ZYGGY_INSTANCE_DIR"] = Path.Combine(_root, "checkout", "instance"),
            ["HOME"] = _root,
            ["CREDENTIALS_DIRECTORY"] = "/run/credentials/zyggy-morning-brief.service",
        }).Environment!;

        // Act
        var request = M365RunRequest.For(M365RunKind.Brief, "/x", instance, _configuration, _partition, "/run/dir");

        // Assert
        request.Environment.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["ZYGGY_HOOKS"] = "off",
            ["ZYGGY_M365_RUN_DIR"] = "/run/dir",
            ["CREDENTIALS_DIRECTORY"] = "/run/credentials/zyggy-morning-brief.service",
        });
    }

    [Theory]
    [InlineData("Brief")]
    [InlineData("MailBackfill")]
    [InlineData("FilesBackfill")]
    public void For_EveryKind_McpConfigIsRunFile_DenyEndsWithLinkedInRules(string kind)
    {
        // Act
        var request = M365RunRequest.For(Enum.Parse<M365RunKind>(kind), "/x", _instance, _configuration, _partition, null);

        // Assert: spec 36 AC-8 — the run loads the m365-only copy, and the linkedin tool and verbs are denied
        request.McpConfig.Should().Be(Path.Join(_instance.Paths.StateDirectory, "run-mcp.json"));
        request.DisallowedTools.Should().ContainInOrder("Bash(node *)", "mcp__linkedin__*", "Bash(zyggy linkedin *)");
    }

    [Fact]
    public void For_WorkingDirectoryIsCheckoutCapsAndLimits()
    {
        // Act
        var request = M365RunRequest.For(M365RunKind.Brief, "/x", _instance, _configuration, _partition, null);

        // Assert
        request.WorkingDirectory.Should().Be(_instance.Checkout);
        request.Timeout.Should().Be(TimeSpan.FromMinutes(30), "spec 35: the mail run is shorter than 33's brief");
        request.MaxCaptureBytes.Should().Be(64 * 1024 * 1024);
        request.Isolation.Should().Be(Zyggy.Core.Models.ModelSessionIsolation.NoAutoMemory);
        request.JsonSchema.Should().Be(new Zyggy.Core.Brief.BriefPrompts().MailSchema);
    }

    [Fact]
    public void For_Brief_RunDirectoryReadableMemoryDenied()
    {
        // Act
        var request = M365RunRequest.For(M365RunKind.Brief, "/x", _instance, _configuration, _partition, "/tmp/run.ABC123");

        // Assert
        request.AllowedTools[^1].Should().Be("Read(/tmp/run.ABC123/**)");
        request.DisallowedTools[^1].Should().Be($"Read(//{Path.Join(_instance.Checkout, "memory").Replace('\\', '/').TrimStart('/')}/**)");
        request.DisallowedTools[^1].Should().NotStartWith("Read(///", "an absolute rule path has exactly two leading slashes");
        request.DisallowedTools.Should().Contain("Bash(zyggy brief *)").And.Contain("Bash(zyggy memory *)");
    }

    [Theory]
    [InlineData("MailBackfill")]
    [InlineData("FilesBackfill")]
    public void For_Backfill_UnchangedByTheBriefRules(string kind)
    {
        // Act
        var request = M365RunRequest.For(Enum.Parse<M365RunKind>(kind), "/x", _instance, _configuration, _partition, "/tmp/run.ABC123");

        // Assert
        request.Timeout.Should().Be(TimeSpan.FromMinutes(120));
        request.Isolation.Should().Be(Zyggy.Core.Models.ModelSessionIsolation.None);
        request.JsonSchema.Should().BeNull();
        request.AllowedTools.Should().NotContain("Read(/tmp/run.ABC123/**)");
        request.DisallowedTools.Should().NotContain(t => t.StartsWith("Read(//", StringComparison.Ordinal));
    }
}
