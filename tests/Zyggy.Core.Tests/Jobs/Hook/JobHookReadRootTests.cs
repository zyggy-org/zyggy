using System.Text.Json;

using Zyggy.Core.Jobs;
using Zyggy.Core.Jobs.Hook;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Jobs.Hook;

/// <summary>
/// Spec 18 AC-25 (the guard): with a read root, a file-tool read and a shell command naming a path outside every root are refused
/// with <c>read outside the read root</c>, and both are allowed inside it; without a read root nothing changes.
/// </summary>
public sealed class JobHookReadRootTests : IDisposable
{
    private const string Allowed = "";
    private const string ReadOutside = "read outside the read root";
    private const string NotAllowed = "not in allowed_tools";

    private static readonly string[] Rules = ["Read", "Grep", "Glob", "Bash", "PowerShell", "Write"];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zyggy-read-root", Guid.NewGuid().ToString("N"));

    public JobHookReadRootTests()
    {
        Directory.CreateDirectory(Path.Combine(Project, "src"));
        Directory.CreateDirectory(Path.Combine(Outbound, "people"));
        Directory.CreateDirectory(Other);
        File.WriteAllText(Path.Combine(Project, "notes.md"), "a note\n");
        File.WriteAllText(Question, "a question\n");
    }

    /// <summary>The run directory: outside every read root.</summary>
    private string Project => Path.Combine(_dir, "desk");

    private string Outbound => Path.Combine(_dir, "memory", "outbound");

    private string Other => Path.Combine(_dir, "memory", "outbound-private");

    /// <summary>A read root that is one file.</summary>
    private string Question => Path.Combine(_dir, "question.md");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A leaked temp folder is harmless.
        }
    }

    public static TheoryData<string, string, string> FileReads() => new()
    {
        { "Read", "{outbound}/people/anna.md", Allowed },
        { "Read", "{outbound}", Allowed },
        { "Read", "{question}", Allowed },
        { "Read", "{outbound}/../outbound-private/a.md", ReadOutside },
        { "Read", "{other}/a.md", ReadOutside },
        { "Read", "{project}/notes.md", ReadOutside },
        { "Read", "notes.md", ReadOutside },
        { "Read", "{question}.bak", ReadOutside },
        { "Grep", "{outbound}/people", Allowed },
        { "Grep", "{project}/src", ReadOutside },
        { "Glob", "{other}", ReadOutside },
    };

    [Theory]
    [MemberData(nameof(FileReads))]
    public void Decide_FileToolReadUnderAReadRoot_AllowedInsideRefusedOutside(string tool, string path, string rule)
    {
        // Arrange
        string property = tool == "Read" ? "file_path" : "path";
        string json = JsonSerializer.Serialize(new Dictionary<string, string> { [property] = Fill(path), ["pattern"] = "x" });

        // Act
        HookDecision decision = Decide(tool, json, [Outbound, Question]);

        // Assert
        decision.Rule.Should().Be(rule);
    }

    [Theory]
    [InlineData("Grep")]
    [InlineData("Glob")]
    public void Decide_SearchWithoutAPath_MeansTheRunDirectory_RefusedOutside(string tool)
    {
        // Act
        HookDecision decision = Decide(tool, """{"pattern":"x"}""", [Outbound]);

        // Assert
        decision.Rule.Should().Be(ReadOutside);
    }

    public static TheoryData<string, string, string> ShellCommands() => new()
    {
        { "Bash", "zyggy decisions", Allowed },
        { "Bash", "zyggy decide --reply-to 01M7MH24SZ031J36BSPKGS34WH --kind answer --undo 'nothing to undo' 'Use the outbound note.'", Allowed },
        { "Bash", "cat '{outbound}/people/anna.md'", Allowed },
        { "Bash", "cat '{other}/a.md'", ReadOutside },
        { "Bash", "cat notes.md", ReadOutside },
        { "Bash", "cat ./notes.md", ReadOutside },
        { "Bash", "cat ../desk/notes.md", ReadOutside },
        { "Bash", "cat ~/secret", ReadOutside },
        { "Bash", "zyggy decisions; cat '{other}/a.md'", ReadOutside },
        { "Bash", "tool --file='{other}/a.md'", ReadOutside },
        { "Bash", "tool --file='{outbound}/people/anna.md'", Allowed },
        { "Bash", "cat $(ls)", ReadOutside },
        { "PowerShell", "Get-Content '{other}/a.md'", ReadOutside },
        { "PowerShell", "Get-Content '{outbound}/people/anna.md'", Allowed },
    };

    [Theory]
    [MemberData(nameof(ShellCommands))]
    public void Decide_ShellCommandUnderAReadRoot_RefusedWhenAWordNamesAPathOutside(string tool, string command, string rule)
    {
        // Arrange
        string json = JsonSerializer.Serialize(new { command = Fill(command) });

        // Act
        HookDecision decision = Decide(tool, json, [Outbound, Question]);

        // Assert
        decision.Rule.Should().Be(rule, Fill(command));
    }

    // A search pattern is part of what is read: it may not climb out of its folder or name another place.
    [Theory]
    [InlineData("Glob", "pattern", "**/*.md", Allowed)]
    [InlineData("Glob", "pattern", "../../desk/**", ReadOutside)]
    [InlineData("Glob", "pattern", "people/../../outbound-private/*", ReadOutside)]
    [InlineData("Glob", "pattern", "/etc/*", ReadOutside)]
    [InlineData("Glob", "pattern", "~/*", ReadOutside)]
    [InlineData("Grep", "glob", "*.md", Allowed)]
    [InlineData("Grep", "glob", "../*.md", ReadOutside)]
    [InlineData("Grep", "pattern", "\\.\\./secret", Allowed)]
    public void Decide_SearchPatternUnderAReadRoot_RefusedWhenItLeavesItsFolder(string tool, string property, string value, string rule)
    {
        // Arrange: the search itself starts inside the root
        var input = new Dictionary<string, string> { ["path"] = Outbound, ["pattern"] = "x" };
        input[property] = value;

        // Act
        HookDecision decision = Decide(tool, JsonSerializer.Serialize(input), [Outbound]);

        // Assert
        decision.Rule.Should().Be(rule);
    }

    // A quoted word with a blank that exists on disk is a path, not a sentence.
    [Fact]
    public void Decide_QuotedPathWithABlankThatExistsOutside_Refused()
    {
        // Arrange
        File.WriteAllText(Path.Combine(Project, "my notes.md"), "a note\n");

        // Act
        HookDecision decision = Decide("Bash", """{"command":"cat 'my notes.md'"}""", [Outbound]);

        // Assert
        decision.Rule.Should().Be(ReadOutside);
    }

    // A sentence with blanks is text, not a path, also when it holds a slash.
    [Fact]
    public void Decide_QuotedSentenceWithASlash_NotTakenAsAPath()
    {
        // Act
        HookDecision decision = Decide("Bash", """{"command":"zyggy decide --undo 'nothing to undo' 'Use /api/v2 for the booking pool.'"}""", [Outbound]);

        // Assert
        decision.Rule.Should().Be(Allowed);
    }

    // The read root narrows a call the rules allow; it never allows one they refuse, and it leaves the write rule alone.
    [Theory]
    [InlineData("Edit", "{outbound}/people/anna.md", NotAllowed)]
    [InlineData("Write", "{outbound}/people/anna.md", "write outside the project")]
    public void Decide_UnderAReadRoot_TheOtherRulesStillApply(string tool, string path, string rule)
    {
        // Arrange
        string json = JsonSerializer.Serialize(new { file_path = Fill(path), content = "x" });

        // Act
        HookDecision decision = Decide(tool, json, [Outbound]);

        // Assert
        decision.Rule.Should().Be(rule);
    }

    [Theory]
    [InlineData("Read", """{"file_path":"notes.md"}""")]
    [InlineData("Bash", """{"command":"cat notes.md"}""")]
    [InlineData("Bash", """{"command":"cat $(ls)"}""")]
    public void Decide_NoReadRoot_ReadsAreNotRestricted(string tool, string json)
    {
        // Act
        HookDecision decision = Decide(tool, json, []);

        // Assert
        decision.Rule.Should().Be(Allowed);
    }

    [Fact]
    public async Task Run_ReadRootOption_ReadOutsideExitTwoWithItsRuleInTheBlockedFile()
    {
        // Arrange
        string blockedFile = Path.Combine(_dir, "run.blocked");
        var console = new VerbConsole(Call("Read", new { file_path = Path.Combine(Project, "notes.md") }));
        string[] args = ["pre-tool-use", "--root", Project, "--blocked-file", blockedFile, "--allow", "Read", "--read-root", Outbound, "--read-root", Question];

        // Act
        int exit = await new JobHookVerb(ignoreCase: false).RunAsync(args, console.Io, Ct);

        // Assert
        (exit, File.ReadAllText(blockedFile)).Should().Be((2, $"Read: read outside the read root ({Path.Combine(Project, "notes.md")})\n"));
    }

    [Fact]
    public async Task Run_ReadRootOption_ReadInsideExitZero()
    {
        // Arrange
        var console = new VerbConsole(Call("Read", new { file_path = Question }));
        string[] args = ["pre-tool-use", "--root", Project, "--blocked-file", Path.Combine(_dir, "run.blocked"), "--allow", "Read", "--read-root", Outbound, "--read-root", Question];

        // Act
        int exit = await new JobHookVerb(ignoreCase: false).RunAsync(args, console.Io, Ct);

        // Assert
        (exit, console.Stderr).Should().Be((0, ""));
    }

    // Fail closed: a read root that is not absolute is a hook error, never an unrestricted run.
    [Fact]
    public async Task Run_RelativeReadRoot_HookErrorExitTwo()
    {
        // Arrange
        var console = new VerbConsole(Call("Read", new { file_path = Question }));
        string[] args = ["pre-tool-use", "--root", Project, "--blocked-file", Path.Combine(_dir, "run.blocked"), "--allow", "Read", "--read-root", "memory/outbound"];

        // Act
        int exit = await new JobHookVerb(ignoreCase: false).RunAsync(args, console.Io, Ct);

        // Assert
        (exit, console.Stderr).Should().Be((2, "zyggy: blocked Read: hook error (--read-root must be an absolute path)\n"));
    }

    private static string Call(string tool, object input) =>
        JsonSerializer.Serialize(new { hook_event_name = "PreToolUse", tool_name = tool, tool_input = input });

    private HookDecision Decide(string tool, string json, string[] readRoots)
    {
        using var document = JsonDocument.Parse(json);
        return new JobHookPolicy(Project, Rules, ignoreCase: false, readRoots).Decide(tool, document.RootElement, Project);
    }

    private string Fill(string text) => text
        .Replace("{outbound}", Outbound.Replace('\\', '/'), StringComparison.Ordinal)
        .Replace("{other}", Other.Replace('\\', '/'), StringComparison.Ordinal)
        .Replace("{question}", Question.Replace('\\', '/'), StringComparison.Ordinal)
        .Replace("{project}", Project.Replace('\\', '/'), StringComparison.Ordinal);
}
