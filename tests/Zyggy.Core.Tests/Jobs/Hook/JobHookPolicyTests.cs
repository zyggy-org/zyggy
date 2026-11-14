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

    // Reviews of 2026-11-14: a pattern rule vouches only for a simple command — plain words and plain quoted strings, nothing the
    // hook would have to interpret. Each row below was a way around the hook, or is a construct the hook does not read.
    public static TheoryData<string, string> NotSimpleCommands() => new()
    {
        // first review: a backslash before a quote, a quote that never closes, ANSI-C quoting
        { "Bash", "dotnet a\\'b; rm -rf x" },
        { "Bash", "dotnet a\\'$(rm x)" },
        { "Bash", "dotnet a\\\"b && rm x" },
        { "Bash", "dotnet test 'never closed; rm x" },
        { "Bash", "dotnet test \"never closed; rm x" },
        { "Bash", "dotnet test $'a\\x3b rm x'" },
        { "Bash", "dotnet test $\"a\"" },

        // second review: comments, line continuation, grouping, smart quotes, prefix characters, wildcards, empty expansions
        { "Bash", "dotnet test a #'\nrm -rf x\n#'" },
        { "Bash", "dotnet test a #\"\nrm -rf x\n#\"" },
        { "Bash", "dotnet test \\\nrm x" },
        { "Bash", "dotnet test \\\r\nrm x" },
        { "Bash", "dotnet test (rm x)" },
        { "Bash", "dotnet test { rm x; }" },
        { "Bash", "dotnet test 'a\u2019; rm x; echo \u2018b'" },
        { "Bash", "dotnet test -c alias.x=!rm" },
        { "Bash", "dotnet test /usr/bin/c?rl" },
        { "Bash", "dotnet test /usr/bin/cur[l]" },
        { "Bash", "dotnet test *.csproj" },
        { "Bash", "dotnet test cu$@rl" },
        { "Bash", "dotnet test $HOME" },
        { "Bash", "dotnet test \"$HOME\"" },
        { "Bash", "dotnet test \"a`rm x`\"" },
        { "Bash", "dotnet test \"a\\\"; rm x\"" },
        { "Bash", "dotnet test \\; rm x" },
        { "Bash", "dotnet test ~/x" },
        { "Bash", "dotnet test a^b" },

        // redirections: a shell write is not path-checked, so a pattern rule does not vouch for one
        { "Bash", "dotnet test > .git/hooks/pre-commit" },
        { "Bash", "dotnet test 2>&1" },
        { "Bash", "dotnet test < input.txt" },
        { "Bash", "dotnet test <<EOF\nrm x\nEOF" },

        // characters outside printable ASCII, and a line break inside a quote
        { "Bash", "dotnet test caf\u00e9" },
        { "Bash", "dotnet test a\u200bb" },
        { "Bash", "dotnet test a\u00a0b" },
        { "Bash", "dotnet test \"a\u201d; rm x; echo \u201cb\"" },
        { "Bash", "dotnet test \"two\nlines\"" },
        { "Bash", "dotnet test 'two\r\nlines'" },
        { "Bash", "dotnet test a\0b" },
        { "Bash", "dotnet test a\u001bb" },

        // PowerShell
        { "PowerShell", "dotnet test (rm x)" },
        { "PowerShell", "dotnet (rm x)" },
        { "PowerShell", "dotnet 'a\u2019; rm x; echo \u2018b'" },
        { "PowerShell", "dotnet \"a\u201d; rm x; echo \u201cb\"" },
        { "PowerShell", "dotnet a #'\nrm x\n#'" },
        { "PowerShell", "dotnet a <# ' #>; rm x; <# ' #>" },
        { "PowerShell", "dotnet a`\"b; rm x" },
        { "PowerShell", "dotnet \"a`tb\"" },
        { "PowerShell", "dotnet \"a`\"; rm x" },
        { "PowerShell", "dotnet 'never closed; rm x" },
        { "PowerShell", "dotnet $x" },
        { "PowerShell", "dotnet \"$(rm x)\"" },
        { "PowerShell", "dotnet @args" },
        { "PowerShell", "dotnet @\"\nx\n\"@" },
        { "PowerShell", "dotnet { rm x }" },
        { "PowerShell", "dotnet test > out.txt" },
        { "PowerShell", "dotnet *.csproj" },
    };

    [Theory]
    [MemberData(nameof(NotSimpleCommands))]
    public void Decide_NotASimpleCommandUnderAPatternRule_Denied(string tool, string command)
    {
        // Act
        var decision = Decide(tool, new { command }, tool + "(dotnet *)");

        // Assert
        new { decision.Rule, SaysWhy = decision.Detail.StartsWith("not a simple command (", StringComparison.Ordinal) }
            .Should().BeEquivalentTo(new { Rule = NotAllowed, SaysWhy = true });
    }

    // What ordinary jobs write, and must keep passing under a pattern rule.
    public static TheoryData<string, string> SimpleCommands() => new()
    {
        { "Bash", "dotnet test" },
        { "Bash", "dotnet build && dotnet test" },
        { "Bash", "dotnet build -c Release /p:Version=1.2.3 --no-restore" },
        { "Bash", "dotnet test --filter \"A&B\"" },
        { "Bash", "dotnet test --filter \"FullyQualifiedName~Hook|FullyQualifiedName~Setup\"" },
        { "Bash", "dotnet test --logger \"trx;LogFileName=a.trx\"" },
        { "Bash", "dotnet test -- x=1,y=2 +z 50% a@b" },
        { "Bash", "dotnet tool run thing -m \"it's done\"" },
        { "Bash", "dotnet tool run thing -m 'costs $5 (really) #1 \\o/ *?'" },
        { "Bash", "dotnet tool run thing -m \"a (b) [c] {d} #e !f *g ~h <i> ^j\"" },
        { "Bash", "dotnet test src/Zyggy.Core/Zyggy.Core.csproj\t--no-build" },
        { "Bash", "dotnet build; dotnet test\ndotnet pack\r\ndotnet --info" },
        { "PowerShell", "dotnet build C:\\proj\\a.csproj" },
        { "PowerShell", "dotnet test --filter 'it''s; fine'" },
        { "PowerShell", "dotnet \"a\"\"; b\"" },
        { "PowerShell", "dotnet a\\'b; rm x'" },
        { "PowerShell", "dotnet build -c Release; dotnet test" },
    };

    [Theory]
    [MemberData(nameof(SimpleCommands))]
    public void Decide_SimpleCommandUnderAPatternRule_Allowed(string tool, string command)
    {
        // Act
        var decision = Decide(tool, new { command }, tool + "(dotnet *)");

        // Assert
        decision.Should().Be(HookDecision.Allow);
    }

    [Theory]
    [InlineData("Bash", "dotnet test 'x'; rm x")]
    [InlineData("Bash", "dotnet test \"a\" && rm x")]
    [InlineData("PowerShell", "dotnet 'it''s'; rm x")]
    [InlineData("PowerShell", "dotnet \"a\"\"\"; rm x")]
    public void Decide_SimpleCommandWithAPartNoRuleMatches_DeniedWithTheCommandAsDetail(string tool, string command)
    {
        // Act
        var decision = Decide(tool, new { command }, tool + "(dotnet *)");

        // Assert
        decision.Should().Be(HookDecision.Deny(NotAllowed, command));
    }

    [Theory]
    [InlineData("dotnet test (rm x)", "not a simple command ('('): dotnet test (rm x)")]
    [InlineData("dotnet test caf\u00e9", "not a simple command (U+00E9): dotnet test caf\u00e9")]
    [InlineData("dotnet test 'open", "not a simple command (a quote never closes): dotnet test 'open")]
    [InlineData("dotnet test \"a\nb\"", "not a simple command (a line break inside a quote): dotnet test \"a b\"")]
    public void Decide_NotASimpleCommand_DetailNamesWhatIsNotRead(string command, string detail)
    {
        // Act
        var decision = Decide("Bash", new { command }, "Bash(dotnet *)");

        // Assert
        decision.Detail.Should().Be(detail);
    }

    [Theory]
    [InlineData("Bash(dotnet:*)", "dotnet=1 rm -rf x")]
    [InlineData("Bash(dotnet*)", "dotnet=1 rm -rf x")]
    [InlineData("Bash(dotnet *)", "dotnet test; X=1 dotnet test")]
    [InlineData("Bash(dotnet *)", "\"dotnet\"=1 rm x")]
    public void Decide_AssignmentBeforeTheCommand_NotSimple(string rule, string command)
    {
        // Act: "name=value program" runs the program; a pattern must describe the program itself
        var decision = Decide("Bash", new { command }, rule);

        // Assert
        decision.Detail.Should().StartWith("not a simple command (an assignment before the command)");
    }

    [Theory]
    [InlineData("Bash", "Bash(*)")]
    [InlineData("Bash", "Bash")]
    [InlineData("PowerShell", "PowerShell(*)")]
    public void Decide_NotASimpleCommandWithAnyCommandRule_Allowed(string tool, string rule)
    {
        // Act
        var decision = Decide(tool, new { command = "dotnet test (echo x) > out.txt # done" }, rule, tool + "(dotnet *)");

        // Assert
        decision.Should().Be(HookDecision.Allow);
    }

    [Theory]
    [InlineData("Bash(*)")]
    [InlineData("Bash")]
    public void Decide_UnreadableQuotingWithAnyCommandRule_Allowed(string rule)
    {
        // Act
        var decision = Decide("Bash", new { command = "echo 'never closed" }, rule);

        // Assert
        decision.Should().Be(HookDecision.Allow);
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

        // Review of 2026-11-14 (finding 3): the three protected places count at any depth, not only at the top of the run directory.
        { "Write", "sub/.git/config", WriteOutside },
        { "Edit", "sub/.git/hooks/pre-commit", WriteOutside },
        { "Write", "a/b/.git", WriteOutside },
        { "Write", "a/b/.claude/settings.local.json", WriteOutside },
        { "Write", "packages/x/.claude/hooks/guard.sh", WriteOutside },
        { "Write", "src/my.git.txt", Allowed },
        { "Write", "docs/.gitignore", Allowed },
        { "Write", "sub/.github/workflows/ci.yml", Allowed },
        { "Write", "sub/.claude/agents/reviewer.md", Allowed },
        { "Write", "src/settings.json", Allowed },
        { "Write", "sub/hooks/build.sh", Allowed },
        { "Write", "sub/.claude/hooksx/a.sh", Allowed },

        // Second review: alternate-stream spellings and names Windows trims reach the same protected files.
        { "Write", ".claude/settings.json::$DATA", WriteOutside },
        { "Write", ".claude/settings.json:", WriteOutside },
        { "Write", ".git::$INDEX_ALLOCATION/config", WriteOutside },
        { "Write", ".claude::$INDEX_ALLOCATION/settings.json", WriteOutside },
        { "Write", ".claude/hooks::$INDEX_ALLOCATION/x", WriteOutside },
        { "Write", "src/a.cs:stream", WriteOutside },
        { "Write", ".git /config", WriteOutside },
        { "Write", ".git./config", WriteOutside },
        { "Write", ".claude/settings.json.", WriteOutside },
        { "Write", ".claude/settings.json ", WriteOutside },
        { "Write", "src/a.b/c.d.cs", Allowed },
        { "Write", "src/my file.cs", Allowed },
        { "Write", "src/./a.cs", Allowed },
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

        // Review of 2026-11-14 (finding 2): quote and escape characters inside the name do not hide the program.
        { "Bash", "env cu\"\"rl http://x", "Bash(env *)" },
        { "Bash", "env cu\"\"rl http://x", "Bash" },
        { "Bash", "env c'u'rl x", "Bash" },
        { "Bash", "env cu\\rl x", "Bash" },
        { "Bash", "env \"curl\" x", "Bash" },
        { "Bash", "xargs w''get", "Bash(xargs *)" },
        { "Bash", "command w\"g\"et x", "Bash" },
        { "Bash", "/usr/bin/cu''rl x", "Bash" },
        { "PowerShell", "cur`l x", "PowerShell" },
        { "PowerShell", "i\"\"wr x", "PowerShell" },
        { "PowerShell", "Invoke-Web''Request x", "PowerShell" },
        { "PowerShell", "& 'Invoke-RestMethod' x", "PowerShell" },
        { "PowerShell", "cmd /c cu^rl x", "PowerShell" },

        // Second review: a prefix character, a line continuation inside the name, a rule that only starts with the name.
        { "Bash", "git -c \"alias.x=!curl http://x\" x", "Bash(git *)" },
        { "Bash", "git -c core.pager=curl log", "Bash(git *)" },
        { "Bash", "env cu\\\nrl http://x", "Bash" },
        { "Bash", "env cu\\\r\nrl http://x", "Bash" },
        { "Bash", "env \"cu\\\nrl\" http://x", "Bash" },
        { "Bash", "env /usr/bin/curl:x", "Bash" },
        { "Bash", "env x@curl", "Bash" },
        { "PowerShell", "Microsoft.PowerShell.Utility\\Invoke-WebRequest x", "PowerShell" },
    };

    [Fact]
    public void Decide_NetworkProgramUnderARuleThatOnlyStartsWithItsName_Denied()
    {
        // Act: second review — Bash(curl-wrapper *) is no rule for curl
        var decision = Decide("Bash", new { command = "env curl http://x" }, "Bash(env *)", "Bash(curl-wrapper *)");

        // Assert
        decision.Rule.Should().Be(Network);
    }

    [Fact]
    public void Decide_NetworkProgramInAPartItsRuleDoesNotMatch_Denied()
    {
        // Act: the curl rule covers the first part only
        var decision = Decide("Bash", new { command = "curl https://api.example.invalid; env curl http://x" }, "Bash(curl *)", "Bash(env *)");

        // Assert
        decision.Rule.Should().Be(Network);
    }

    [Theory]
    [InlineData("curl https://a.example.invalid && curl https://b.example.invalid", "Bash(curl *)")]
    [InlineData("echo start; curl https://a.example.invalid", "Bash", "Bash(curl *)")]
    [InlineData("git commit -m \"confirm the libcurl firmware fix\"", "Bash(git *)")]
    [InlineData("dotnet add package Curly.Wgetter", "Bash(dotnet *)")]
    public void Decide_NetworkWordCoveredByItsRuleOrOnlyPartOfAnotherWord_Allowed(string command, params string[] rules)
    {
        // Act
        var decision = Decide("Bash", new { command }, rules);

        // Assert
        decision.Should().Be(HookDecision.Allow);
    }

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

    [Theory]
    [InlineData("Bash", "cu\"\"rl https://api.example.invalid", "Bash(curl *)")]
    [InlineData("Bash", "echo \"a 'quoted' word\" && echo done", "Bash(echo *)")]
    [InlineData("Bash", "git commit -m \"x && y\"", "Bash(git *)")]
    public void Decide_QuotedWordsWithoutAnUnnamedNetworkProgram_NotDeniedAsNetwork(string tool, string command, string rule)
    {
        // Act
        var decision = Decide(tool, new { command }, rule);

        // Assert
        decision.Rule.Should().NotBe(Network);
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
