using Zyggy.Core.Models;

namespace Zyggy.Core.Tests.Models;

/// <summary>An absolute path in a Claude Code permission rule starts with exactly two slashes.</summary>
public sealed class ClaudeRulesTests
{
    [Theory]
    [InlineData("/opt/zyggy/checkout/memory", "//opt/zyggy/checkout/memory")]
    [InlineData("/home/zyggy/.local/state", "//home/zyggy/.local/state")]
    [InlineData(@"D:\zyggy\memory", "//D:/zyggy/memory")]
    public void Absolute_ExactlyTwoLeadingSlashes(string path, string expected)
    {
        // Act / Assert
        ClaudeRules.Absolute(path).Should().Be(expected);
    }
}
