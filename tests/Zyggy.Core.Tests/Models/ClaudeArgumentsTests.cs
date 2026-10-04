using System.Reflection;

using Zyggy.Core.Models;

namespace Zyggy.Core.Tests.Models;

public sealed class ClaudeArgumentsTests
{
    private static readonly string[] FixedPrefix =
    [
        "-p",
        "--output-format", "stream-json", "--verbose",
        "--permission-mode", "auto",
        "--permission-prompts", "none",
        "--no-session-persistence",
        "--max-turns", "60",
    ];

    private static ModelRunRequest Minimal() => new("the prompt", "/run", TimeSpan.FromMinutes(1));

    [Fact]
    public void Build_MinimalRequest_IsFixedPrefixInContractOrder()
    {
        // Act
        var args = ClaudeArguments.Build(Minimal());

        // Assert
        args.Should().Equal(FixedPrefix);
    }

    [Fact]
    public void Build_EveryFieldSet_AppendsOptionalFlagsInContractOrder()
    {
        // Arrange
        var request = Minimal() with
        {
            MaxBudgetUsd = 5m,
            Tools = ["Read", "Grep", "Glob"],
            AllowedTools = ["Read", "Bash(git status)"],
            AdditionalDirectories = ["/mem/a", "/mem/b"],
            JsonSchema = """{"type":"object"}""",
            Model = "sonnet",
            AppendSystemPrompt = "You file facts.",
            Isolation = ModelSessionIsolation.NoMcp | ModelSessionIsolation.NoHooks | ModelSessionIsolation.NoAutoMemory
                | ModelSessionIsolation.NoSlashCommands,
        };

        // Act
        var args = ClaudeArguments.Build(request);

        // Assert
        args.Should().Equal(
        [
            .. FixedPrefix,
            "--max-budget-usd", "5",
            "--tools", "Read,Grep,Glob",
            "--allowedTools", "Read,Bash(git status)",
            "--add-dir", "/mem/a",
            "--add-dir", "/mem/b",
            "--json-schema", """{"type":"object"}""",
            "--model", "sonnet",
            "--append-system-prompt", "You file facts.",
            "--strict-mcp-config", "--disallowedTools", "mcp__*",
            "--settings", """{"disableAllHooks":true,"autoMemoryEnabled":false}""",
            "--disable-slash-commands",
        ]);
    }

    [Fact]
    public void Build_BudgetWithFraction_IsFormattedInvariant()
    {
        // Act
        var args = ClaudeArguments.Build(Minimal() with { MaxBudgetUsd = 2.5m });

        // Assert
        args.Should().ContainInConsecutiveOrder("--max-budget-usd", "2.5");
    }

    [Fact]
    public void Build_ToolsEmpty_EmitsEmptyToolsValue()
    {
        // Act
        var args = ClaudeArguments.Build(Minimal() with { Tools = [] });

        // Assert
        args.Should().Equal([.. FixedPrefix, "--tools", ""]);
    }

    [Fact]
    public void Build_ToolsNull_OmitsToolsFlag()
    {
        // Act
        var args = ClaudeArguments.Build(Minimal() with { Tools = null });

        // Assert
        args.Should().NotContain("--tools");
    }

    [Fact]
    public void Build_NoHooksOnly_SettingsHasOnlyDisableAllHooks()
    {
        // Act
        var args = ClaudeArguments.Build(Minimal() with { Isolation = ModelSessionIsolation.NoHooks });

        // Assert
        args.Should().Equal([.. FixedPrefix, "--settings", """{"disableAllHooks":true}"""]);
    }

    public static TheoryData<int> IsolationCombinations()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < 16; i++)
        {
            data.Add(i);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(IsolationCombinations))]
    public void Build_AnyRequest_NeverContainsForbiddenFlags(int isolation)
    {
        // Arrange
        var request = Minimal() with
        {
            Isolation = (ModelSessionIsolation)isolation,
            Tools = ["Read"],
            AllowedTools = ["Read"],
            JsonSchema = "{}",
            MaxBudgetUsd = 1m,
        };
        string[] forbidden =
        [
            "--dangerously-skip-permissions", "--allow-dangerously-skip-permissions", "--bare", "--safe-mode", "--resume", "--continue",
        ];

        // Act
        var args = ClaudeArguments.Build(request);

        // Assert
        args.Should().NotIntersectWith(forbidden);
        args.Should().NotContain(a => a.Contains("bypassPermissions", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_PromptText_NeverAppearsInArguments()
    {
        // Arrange
        var request = Minimal() with { Prompt = "SECRET-PROMPT-TEXT" };

        // Act
        var args = ClaudeArguments.Build(request);

        // Assert
        args.Should().NotContain(a => a.Contains("SECRET-PROMPT-TEXT", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_ValueStartingWithDash_ThrowsArgumentException()
    {
        // Act
        Action model = () => ClaudeArguments.Build(Minimal() with { Model = "--bare" });
        Action tool = () => ClaudeArguments.Build(Minimal() with { Tools = ["-x"] });
        Action allowed = () => ClaudeArguments.Build(Minimal() with { AllowedTools = ["-z"] });
        Action dir = () => ClaudeArguments.Build(Minimal() with { AdditionalDirectories = ["-y"] });

        // Assert
        model.Should().Throw<ArgumentException>();
        tool.Should().Throw<ArgumentException>();
        allowed.Should().Throw<ArgumentException>();
        dir.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ModelRunRequest_PublicSurface_HasNoPropertyForForbiddenFlags()
    {
        // Arrange
        string[] words = ["Skip", "Bypass", "Bare", "Resume", "Continue"];

        // Act
        var names = typeof(ModelRunRequest).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name);

        // Assert
        names.Should().NotContain(n => words.Any(w => n.Contains(w, StringComparison.OrdinalIgnoreCase)));
    }
}
