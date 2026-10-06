using Zyggy.Core.Models;

namespace Zyggy.Core.Tests.Models;

/// <summary>
/// Spec 33 (found on Central): the m365 runs load the project's <c>.mcp.json</c> through <c>--mcp-config</c>, because Claude Code gives a
/// project-scope headersHelper an environment without <c>CREDENTIALS_DIRECTORY</c>; without <c>McpConfig</c> the argument list is unchanged.
/// </summary>
public sealed class ClaudeArgumentsMcpConfigTests
{
    private static ModelRunRequest Request(ModelSessionIsolation isolation = ModelSessionIsolation.None) =>
        new("p", ".", TimeSpan.FromMinutes(1)) { AllowedTools = ["Read"], MaxTurns = 40, Isolation = isolation };

    [Fact]
    public void Build_McpConfig_StrictThenConfigLast()
    {
        // Act
        var args = ClaudeArguments.Build(Request() with { McpConfig = "/srv/agent/central/.mcp.json" });

        // Assert
        args.TakeLast(3).Should().Equal("--strict-mcp-config", "--mcp-config", "/srv/agent/central/.mcp.json");
        args.Count(a => a == "--strict-mcp-config").Should().Be(1);
    }

    [Fact]
    public void Build_NoMcpConfig_ArgumentsAsBefore()
    {
        // Act
        var args = ClaudeArguments.Build(Request());

        // Assert
        args.Should().NotContain("--mcp-config").And.NotContain("--strict-mcp-config");
    }

    [Fact]
    public void Build_McpConfigWithNoMcp_Throws()
    {
        // Act
        var build = () => ClaudeArguments.Build(Request(ModelSessionIsolation.NoMcp) with { McpConfig = "/x/.mcp.json" });

        // Assert
        build.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Build_McpConfigStartingWithDash_Throws()
    {
        // Act
        var build = () => ClaudeArguments.Build(Request() with { McpConfig = "--dangerously-skip-permissions" });

        // Assert
        build.Should().Throw<ArgumentException>();
    }
}
