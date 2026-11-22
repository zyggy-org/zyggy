using Zyggy.Core.Outlook;

namespace Zyggy.Core.Tests.Outlook;

/// <summary>
/// Spec 58 Q24 (accepted by the owner 2026-11-22, widened by Central to every tool): the session that may send or move work mail keeps
/// every tool use beyond Read and the <c>zyggy outlook</c> read verbs on the owner's prompt. The guard refuses a send or move when the
/// session runs in another permission mode, or when a settings file auto-allows anything else (bare Bash, PowerShell, Write, Edit, an
/// MCP tool, a wider Bash rule) that its deny rules do not take back.
/// </summary>
public sealed class OutlookSessionSettingsTests
{
    private const string Recommended = """
        {
          "permissions": {
            "defaultMode": "default",
            "disableBypassPermissionsMode": "disable",
            "allow": ["Read", "Bash(zyggy outlook policy)", "Bash(zyggy outlook count *)", "Bash(zyggy outlook list:*)", "Bash(zyggy.exe outlook brief-done *)"],
            "deny": ["Write", "Edit", "NotebookEdit", "mcp__plugin_playwright_playwright__browser_run_code_unsafe"]
          }
        }
        """;

    [Fact]
    public void Problem_DefaultModeAndTheRecommendedSettings_None()
    {
        // Act
        string? problem = OutlookSessionSettings.Problem("default", [new("project", Recommended), new("user", null)]);

        // Assert
        problem.Should().BeNull();
    }

    [Theory]
    [InlineData("acceptEdits")]
    [InlineData("bypassPermissions")]
    [InlineData("dontAsk")]
    [InlineData("auto")]
    [InlineData("plan")]
    [InlineData(null)]
    public void Problem_OtherPermissionMode_Refused(string? mode)
    {
        // Act
        string? problem = OutlookSessionSettings.Problem(mode, [new("project", Recommended)]);

        // Assert
        problem.Should().Contain("permission mode");
    }

    [Theory]
    [InlineData("Bash")]
    [InlineData("Bash(*)")]
    [InlineData("Bash(:*)")]
    [InlineData("PowerShell")]
    [InlineData("PowerShell(Get-Content *)")]
    [InlineData("Write")]
    [InlineData("Edit(src/**)")]
    [InlineData("MultiEdit")]
    [InlineData("NotebookEdit")]
    [InlineData("WebFetch")]
    [InlineData("mcp__plugin_playwright_playwright__browser_run_code_unsafe")]
    [InlineData("mcp__softeria")]
    [InlineData("Bash(git *)")]
    [InlineData("Bash(zyggy outlook *)")]
    [InlineData("Bash(zyggy outlook send *)")]
    [InlineData("Bash(zyggy outlook list; bash x)")]
    [InlineData("Bash(python3 *)")]
    public void Problem_AutoAllowedBeyondTheReads_Refused(string rule)
    {
        // Arrange
        string user = "{ \"permissions\": { \"allow\": [\"" + rule + "\"] } }";

        // Act
        string? problem = OutlookSessionSettings.Problem("default", [new("project", """{ "permissions": { "allow": ["Read"] } }"""), new("user settings", user)]);

        // Assert
        problem.Should().Contain("user settings").And.Contain(rule);
    }

    [Fact]
    public void Problem_AllowTakenBackByADeny_None()
    {
        // Arrange: the user file allows Write everywhere; the project denies it, and deny wins in Claude Code.
        string user = """{ "permissions": { "allow": ["Write", "Edit(src/**)"] } }""";

        // Act
        string? problem = OutlookSessionSettings.Problem("default", [new("project", Recommended), new("user", user)]);

        // Assert
        problem.Should().BeNull();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "permissions": { "allow": "Bash" } }""")]
    [InlineData("""{ "permissions": { "allow": [1] } }""")]
    public void Problem_UnreadableSettings_Refused(string text)
    {
        // Act
        string? problem = OutlookSessionSettings.Problem("default", [new("local", text)]);

        // Assert
        problem.Should().Contain("local");
    }

    [Fact]
    public void Problem_FileTheGuardCannotRead_Refused()
    {
        // Act
        string? problem = OutlookSessionSettings.Problem("default", [new("managed", null, Unreadable: true)]);

        // Assert
        problem.Should().Contain("managed");
    }
}
