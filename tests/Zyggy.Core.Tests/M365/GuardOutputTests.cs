using System.Text;

using Zyggy.Core.M365.Guard;

namespace Zyggy.Core.Tests.M365;

/// <summary>The deny line, byte for byte as <c>jq -nc</c> prints it (expected bytes written by hand).</summary>
public sealed class GuardOutputTests
{
    [Theory]
    [InlineData("Bcc is not allowed")]
    [InlineData("target exists (would overwrite)")]
    [InlineData("actions missing (consent is obsolete — D7)")]
    public void Deny_BytesExact(string reason)
    {
        // Act
        var line = GuardOutput.Deny(reason);

        // Assert
        Encoding.UTF8.GetBytes(line).Should().Equal(Encoding.UTF8.GetBytes(
            "{\"hookSpecificOutput\":{\"hookEventName\":\"PreToolUse\",\"permissionDecision\":\"deny\",\"permissionDecisionReason\":\"m365-guard: refused: " +
            reason + "\"}}\n"));
    }

    [Fact]
    public void Deny_QuoteEscapedAsJq()
    {
        // Act
        var line = GuardOutput.Deny("a \"quoted\" name");

        // Assert
        line.Should().Contain("\"permissionDecisionReason\":\"m365-guard: refused: a \\\"quoted\\\" name\"");
    }
}
