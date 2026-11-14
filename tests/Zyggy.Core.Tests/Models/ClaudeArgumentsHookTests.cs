using System.Text.Json;

using Zyggy.Core.Models;

namespace Zyggy.Core.Tests.Models;

/// <summary>Spec 15 AC-7: a job run's <c>PreToolUse</c> hook travels in the one <c>--settings</c> argument; without a hook the arguments are unchanged.</summary>
public sealed class ClaudeArgumentsHookTests
{
    private static readonly ModelHookCommand Hook = new("/opt/zyggy/zyggy", ["hook", "pre-tool-use", "--root", "/src/shop", "--allow", "Read"]);

    private static ModelRunRequest Request(ModelSessionIsolation isolation = ModelSessionIsolation.None) =>
        new("p", ".", TimeSpan.FromMinutes(1)) { AllowedTools = ["Read"], MaxTurns = 40, Isolation = isolation };

    private static string SettingsOf(IReadOnlyList<string> args)
    {
        var index = args.ToList().IndexOf("--settings");
        return args[index + 1];
    }

    [Fact]
    public void Build_PreToolUseHook_SettingsHoldOneCommandHookForEveryTool()
    {
        // Act
        var args = ClaudeArguments.Build(Request() with { PreToolUseHook = Hook });

        // Assert
        SettingsOf(args).Should().Be(
            """{"hooks":{"PreToolUse":[{"hooks":[{"type":"command","command":"/opt/zyggy/zyggy","args":["hook","pre-tool-use","--root","/src/shop","--allow","Read"]}]}]}}""");
    }

    [Fact]
    public void Build_PreToolUseHookWithIsolation_OneSettingsArgumentHoldsBoth()
    {
        // Act
        var args = ClaudeArguments.Build(Request(ModelSessionIsolation.NoAutoMemory) with { PreToolUseHook = Hook });

        // Assert
        using var settings = JsonDocument.Parse(SettingsOf(args));
        new
        {
            Count = args.Count(a => a == "--settings"),
            AutoMemory = settings.RootElement.GetProperty("autoMemoryEnabled").GetBoolean(),
            Command = settings.RootElement.GetProperty("hooks").GetProperty("PreToolUse")[0].GetProperty("hooks")[0].GetProperty("command").GetString(),
        }.Should().BeEquivalentTo(new { Count = 1, AutoMemory = false, Command = "/opt/zyggy/zyggy" });
    }

    [Fact]
    public void Build_PreToolUseHookWithNoHooks_Throws()
    {
        // Act: disableAllHooks would switch the job hook off — a contradiction, refused like McpConfig with NoMcp
        var build = () => ClaudeArguments.Build(Request(ModelSessionIsolation.NoHooks) with { PreToolUseHook = Hook });

        // Assert
        build.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Build_NoHook_NoSettingsArgument()
    {
        // Act
        var args = ClaudeArguments.Build(Request());

        // Assert
        args.Should().NotContain("--settings");
    }
}
