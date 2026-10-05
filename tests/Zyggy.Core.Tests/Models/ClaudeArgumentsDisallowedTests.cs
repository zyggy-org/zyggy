using Zyggy.Core.Models;

namespace Zyggy.Core.Tests.Models;

/// <summary>Spec 33: the additive <c>DisallowedTools</c> of a model request; with none, the argument list is 28's, byte for byte.</summary>
public sealed class ClaudeArgumentsDisallowedTests
{
    private static ModelRunRequest Request(ModelSessionIsolation isolation = ModelSessionIsolation.None) =>
        new("p", ".", TimeSpan.FromMinutes(1)) { AllowedTools = ["Read", "mcp__m365__get-drive-item"], MaxTurns = 40, Isolation = isolation };

    [Fact]
    public void Build_DisallowedTools_OneFlagAfterAllowed()
    {
        // Act
        var args = ClaudeArguments.Build(Request() with { DisallowedTools = ["mcp__m365__send-shared-mailbox-mail", "WebFetch"] });

        // Assert
        var allowed = args.ToList().IndexOf("--allowedTools");
        args[allowed + 2].Should().Be("--disallowedTools");
        args[allowed + 3].Should().Be("mcp__m365__send-shared-mailbox-mail,WebFetch");
        args.Count(a => a == "--disallowedTools").Should().Be(1);
    }

    [Fact]
    public void Build_DisallowedWithNoMcp_SingleFlagMcpStarFirst()
    {
        // Act
        var args = ClaudeArguments.Build(Request(ModelSessionIsolation.NoMcp) with { DisallowedTools = ["WebFetch"] });

        // Assert
        args.Count(a => a == "--disallowedTools").Should().Be(1);
        args[args.ToList().IndexOf("--disallowedTools") + 1].Should().Be("mcp__*,WebFetch");
        args.Should().Contain("--strict-mcp-config");
    }

    [Theory]
    [InlineData(ModelSessionIsolation.None)]
    [InlineData(ModelSessionIsolation.NoMcp)]
    [InlineData(ModelSessionIsolation.NoMcp | ModelSessionIsolation.NoHooks)]
    public void Build_EmptyDisallowed_IdenticalToBefore(ModelSessionIsolation isolation)
    {
        // Arrange: 28's shape — NoMcp alone adds "--strict-mcp-config --disallowedTools mcp__*" after the model options
        var request = Request(isolation);

        // Act
        var args = ClaudeArguments.Build(request);

        // Assert
        if (isolation.HasFlag(ModelSessionIsolation.NoMcp))
        {
            var strict = args.ToList().IndexOf("--strict-mcp-config");
            args.Skip(strict).Take(3).Should().Equal("--strict-mcp-config", "--disallowedTools", "mcp__*");
        }
        else
        {
            args.Should().NotContain("--disallowedTools");
        }
    }

    [Fact]
    public void Build_DisallowedValueStartingWithDash_Throws()
    {
        // Act
        var act = () => ClaudeArguments.Build(Request() with { DisallowedTools = ["--dangerously-skip-permissions"] });

        // Assert
        act.Should().Throw<ArgumentException>();
    }
}
