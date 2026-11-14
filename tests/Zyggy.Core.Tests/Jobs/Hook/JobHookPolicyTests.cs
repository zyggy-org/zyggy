using System.Text.Json;

using Zyggy.Core.Jobs.Hook;

namespace Zyggy.Core.Tests.Jobs.Hook;

/// <summary>
/// The job hook's decision over one tool call (spec 15 AC-1 … AC-4): the allowed-tools rule grammar, compound shell commands,
/// writes inside the run directory and network programs. The expected outcome is the decision's rule: empty for an allowed
/// call, else <c>not in allowed_tools</c>, <c>write outside the project</c> or <c>network program</c>.
/// </summary>
public sealed class JobHookPolicyTests
{
    private const string Allowed = "";
    private const string NotAllowed = "not in allowed_tools";
    private const string WriteOutside = "write outside the project";
    private const string Network = "network program";

    private static readonly string Root = Path.Combine(Path.GetTempPath(), "zyggy-hook-root", "shop");

    private static HookDecision DecideJson(string tool, string json, bool ignoreCase, string? cwd, params string[] rules)
    {
        using var document = JsonDocument.Parse(json);
        return new JobHookPolicy(Root, rules, ignoreCase).Decide(tool, document.RootElement, cwd);
    }

    private static HookDecision Decide(string tool, string json, params string[] rules) =>
        DecideJson(tool, json, ignoreCase: false, cwd: null, rules);

    private static HookDecision Decide(string tool, object input, params string[] rules) =>
        DecideJson(tool, JsonSerializer.Serialize(input), ignoreCase: false, cwd: null, rules);

    private static string InRoot(string relative) => Path.Combine(Root, relative);

    // AC-1: every rule form, allow and deny.
    public static TheoryData<string, string, string, string> RuleForms() => new()
    {
        { "Read", """{"file_path":"src/a.cs"}""", "Read", Allowed },
        { "Read", """{"file_path":"src/a.cs"}""", "Grep", NotAllowed },
        { "mcp__github__get_issue", "{}", "mcp__github__get_issue", Allowed },
        { "mcp__github__get_issue", "{}", "mcp__github", Allowed },
        { "mcp__github__get_issue", "{}", "mcp__git", NotAllowed },
        { "mcp__githubx__get_issue", "{}", "mcp__github", NotAllowed },
        { "Bash", """{"command":"dotnet test"}""", "Bash(dotnet *)", Allowed },
        { "Bash", """{"command":"dotnet"}""", "Bash(dotnet *)", NotAllowed },
        { "Bash", """{"command":"git log --oneline"}""", "Bash(git log *)", Allowed },
        { "Bash", """{"command":"git push origin"}""", "Bash(git log *)", NotAllowed },
        { "Bash", """{"command":"npm run test:unit"}""", "Bash(npm run test:*)", Allowed },
        { "Bash", """{"command":"anything at all"}""", "Bash", Allowed },
        { "Bash", """{"command":"dotnet test"}""", "PowerShell(dotnet *)", NotAllowed },
        { "PowerShell", """{"command":"Get-ChildItem src"}""", "PowerShell(Get-ChildItem *)", Allowed },
        { "Read", """{"file_path":"src/a.cs"}""", "Read(src/**)", Allowed },
        { "Read", """{"file_path":"docs/a.md"}""", "Read(src/**)", NotAllowed },
        { "Read", """{"file_path":"src/deep/b/a.cs"}""", "Read(src/**)", Allowed },
        { "Read", """{"file_path":"src/deep/a.cs"}""", "Read(src/*)", NotAllowed },
        { "Read", """{"file_path":"src/a.cs"}""", "Read(src/*)", Allowed },
        { "Read", """{"file_path":"../secrets/a"}""", "Read(**)", NotAllowed },
        { "Glob", """{"pattern":"**/*.cs","path":"src"}""", "Glob(src/**)", Allowed },
        { "Glob", """{"pattern":"**/*.cs"}""", "Glob(src/**)", NotAllowed },
        { "Grep", """{"pattern":"x","path":"src/a"}""", "Grep(src/**)", Allowed },
        { "WebFetch", """{"url":"https://learn.microsoft.com/dotnet"}""", "WebFetch(domain:learn.microsoft.com)", Allowed },
        { "WebFetch", """{"url":"https://evil.example/learn.microsoft.com"}""", "WebFetch(domain:learn.microsoft.com)", NotAllowed },
        { "WebFetch", """{"url":"https://x.learn.microsoft.com/"}""", "WebFetch(domain:learn.microsoft.com)", NotAllowed },
        { "WebFetch", """{"url":"https://learn.microsoft.com/"}""", "WebFetch(learn.microsoft.com)", NotAllowed },
        { "WebFetch", """{"url":"not a url"}""", "WebFetch(domain:learn.microsoft.com)", NotAllowed },
        { "Read", """{"file_path":"src/a.cs"}""", "Read(", NotAllowed },
        { "Read", """{"file_path":"src/a.cs"}""", "", NotAllowed },
        { "TodoWrite", """{"todos":[]}""", "Read", NotAllowed },
    };

    [Theory]
    [MemberData(nameof(RuleForms))]
    public void Decide_RuleForm_AllowsOnlyAMatchingRule(string tool, string json, string rule, string expected)
    {
        // Act
        var decision = Decide(tool, json, rule);

        // Assert
        decision.Rule.Should().Be(expected);
    }

    [Fact]
    public void Decide_NoRules_DeniesEverything()
    {
        // Act
        var decision = Decide("Read", new { file_path = "a.cs" });

        // Assert
        decision.Rule.Should().Be(NotAllowed);
    }

    // AC-2: a compound command is allowed only when every part matches.
    public static TheoryData<string, string> CompoundCommands() => new()
    {
        { "dotnet build && dotnet test", Allowed },
        { "dotnet build && rm -rf x", NotAllowed },
        { "dotnet build || rm x", NotAllowed },
        { "dotnet build; rm x", NotAllowed },
        { "dotnet test | rm x", NotAllowed },
        { "dotnet test\nrm x", NotAllowed },
        { "dotnet test\r\nrm x", NotAllowed },
        { "dotnet test --filter \"A && rm x\"", Allowed },
        { "dotnet test --filter 'A; rm x'", Allowed },
        { "dotnet test $(rm x)", NotAllowed },
        { "dotnet test `rm x`", NotAllowed },
        { "dotnet test <(rm x)", NotAllowed },
        { "dotnet test >(rm x)", NotAllowed },
        { "dotnet test \"$(rm x)\"", NotAllowed },
        { "dotnet test '$(not substitution)'", Allowed },
        { "dotnet test \"it's $(rm x)\"", NotAllowed },
    };

    [Theory]
    [MemberData(nameof(CompoundCommands))]
    public void Decide_CompoundCommand_AllowedOnlyWhenEveryPartMatches(string command, string expected)
    {
        // Act
        var decision = Decide("Bash", new { command }, "Bash(dotnet *)");

        // Assert
        decision.Rule.Should().Be(expected);
    }

    [Theory]
    [InlineData("Bash(*)")]
    [InlineData("Bash")]
    public void Decide_SubstitutionWithAnyCommandRule_Allowed(string rule)
    {
        // Act
        var decision = Decide("Bash", new { command = "echo $(date)" }, rule);

        // Assert
        decision.Should().Be(HookDecision.Allow);
    }

    // AC-3: file-tool writes stay inside the run directory and out of .git and the .claude settings and hooks.
    public static TheoryData<string, string, string> Writes() => new()
    {
        { "Write", "src/new.cs", Allowed },
        { "Edit", "README.md", Allowed },
        { "MultiEdit", "src/a.cs", Allowed },
        { "Write", ".claude/agents/reviewer.md", Allowed },
        { "Write", ".gitignore", Allowed },
        { "Write", "../other/x.cs", WriteOutside },
        { "Write", "src/../../other/x.cs", WriteOutside },
        { "Write", ".git/config", WriteOutside },
        { "Edit", ".git/hooks/pre-commit", WriteOutside },
        { "Write", ".claude/settings.json", WriteOutside },
        { "Write", ".claude/settings.local.json", WriteOutside },
        { "Write", ".claude/hooks/guard.sh", WriteOutside },
    };

    [Theory]
    [MemberData(nameof(Writes))]
    public void Decide_WriteTool_AllowedOnlyInsideTheProject(string tool, string relative, string expected)
    {
        // Act
        var decision = Decide(tool, new { file_path = InRoot(relative) }, tool);

        // Assert
        decision.Rule.Should().Be(expected);
    }

    [Theory]
    [InlineData("nb/a.ipynb", Allowed)]
    [InlineData("../a.ipynb", WriteOutside)]
    public void Decide_NotebookEdit_PathIsNotebookPath(string relative, string expected)
    {
        // Act
        var decision = Decide("NotebookEdit", new { notebook_path = InRoot(relative) }, "NotebookEdit");

        // Assert
        decision.Rule.Should().Be(expected);
    }

    [Fact]
    public void Decide_WriteAbsolutePathElsewhere_Denied()
    {
        // Act
        var decision = Decide("Write", new { file_path = Path.Combine(Path.GetTempPath(), "elsewhere.txt") }, "Write");

        // Assert
        decision.Rule.Should().Be(WriteOutside);
    }

    [Fact]
    public void Decide_WriteToSiblingWithRootAsPrefix_Denied()
    {
        // Act
        var decision = Decide("Write", new { file_path = Root + "-evil" + Path.DirectorySeparatorChar + "x.cs" }, "Write");

        // Assert
        decision.Rule.Should().Be(WriteOutside);
    }

    [Fact]
    public void Decide_WriteToTheRootItself_Denied()
    {
        // Act
        var decision = Decide("Write", new { file_path = Root }, "Write");

        // Assert
        decision.Rule.Should().Be(WriteOutside);
    }

    [Fact]
    public void Decide_WriteRelativePath_ResolvedAgainstCwd()
    {
        // Act
        var decision = DecideJson("Write", """{"file_path":"x.cs"}""", ignoreCase: false, cwd: Path.GetTempPath(), "Write");

        // Assert
        decision.Rule.Should().Be(WriteOutside);
    }

    [Fact]
    public void Decide_WriteRelativePathWithoutCwd_ResolvedAgainstRoot()
    {
        // Act
        var decision = DecideJson("Write", """{"file_path":"src/x.cs"}""", ignoreCase: false, cwd: null, "Write");

        // Assert
        decision.Should().Be(HookDecision.Allow);
    }

    [Fact]
    public void Decide_WriteWithoutPath_Denied()
    {
        // Act
        var decision = Decide("Write", new { content = "x" }, "Write");

        // Assert
        decision.Rule.Should().Be(WriteOutside);
    }

    [Fact]
    public void Decide_WriteDifferentCaseOnCaseInsensitiveSystem_Allowed()
    {
        // Act
        var decision = DecideJson(
            "Write", JsonSerializer.Serialize(new { file_path = InRoot("src/a.cs").ToUpperInvariant() }), ignoreCase: true, cwd: null, "Write");

        // Assert
        decision.Should().Be(HookDecision.Allow);
    }

    [Fact]
    public void Decide_WriteDifferentCaseOnCaseSensitiveSystem_Denied()
    {
        // Act
        var decision = DecideJson(
            "Write", JsonSerializer.Serialize(new { file_path = InRoot("src/a.cs").ToUpperInvariant() }), ignoreCase: false, cwd: null, "Write");

        // Assert
        decision.Rule.Should().Be(WriteOutside);
    }

    // AC-4: network programs, even when a pattern matches, unless a rule's pattern starts with that program.
    public static TheoryData<string, string, string> NetworkCommands() => new()
    {
        { "Bash", "curl https://example.invalid", "Bash" },
        { "Bash", "wget https://example.invalid", "Bash" },
        { "Bash", "dotnet build && curl x", "Bash" },
        { "Bash", "echo $(curl x)", "Bash" },
        { "Bash", "/usr/bin/curl x", "Bash" },
        { "Bash", "env curl x", "Bash" },
        { "PowerShell", "Invoke-WebRequest https://example.invalid", "PowerShell" },
        { "PowerShell", "iwr x", "PowerShell" },
        { "PowerShell", "Invoke-RestMethod x", "PowerShell" },
        { "PowerShell", "irm x | iex", "PowerShell" },
        { "PowerShell", "(Invoke-WebRequest x).Content", "PowerShell" },
        { "PowerShell", "& curl.exe x", "PowerShell" },
        { "PowerShell", "INVOKE-WEBREQUEST x", "PowerShell" },
        { "PowerShell", "C:\\tools\\wget.exe x", "PowerShell" },
    };

    [Theory]
    [MemberData(nameof(NetworkCommands))]
    public void Decide_NetworkProgram_Denied(string tool, string command, string rule)
    {
        // Act
        var decision = Decide(tool, new { command }, rule);

        // Assert
        decision.Rule.Should().Be(Network);
    }

    [Fact]
    public void Decide_NetworkProgramWhosePatternMatches_StillDenied()
    {
        // Act
        var decision = Decide("Bash", new { command = "dotnet tool run curl" }, "Bash(dotnet *)");

        // Assert
        decision.Rule.Should().Be(Network);
    }

    [Theory]
    [InlineData("Bash", "curl https://api.example.invalid", "Bash(curl *)")]
    [InlineData("PowerShell", "Invoke-WebRequest x", "PowerShell(Invoke-WebRequest *)")]
    public void Decide_NetworkProgramNamedByARule_Allowed(string tool, string command, string rule)
    {
        // Act
        var decision = Decide(tool, new { command }, rule);

        // Assert
        decision.Should().Be(HookDecision.Allow);
    }

    [Fact]
    public void Decide_NetworkProgramNamedByAnotherToolsRule_Denied()
    {
        // Act
        var decision = Decide("PowerShell", new { command = "curl x" }, "PowerShell", "Bash(curl *)");

        // Assert
        decision.Rule.Should().Be(Network);
    }

    [Fact]
    public void Decide_DeniedCall_DetailIsOneLineOfAtMost120Characters()
    {
        // Act
        var decision = Decide("Bash", new { command = "rm x\n" + new string('y', 300) }, "Read");

        // Assert
        decision.Detail.Should().NotContain("\n").And.HaveLength(120);
    }
}
