using Zyggy.Core.Models;

namespace Zyggy.Core.Tests.Models;

/// <summary>Spec 33: the stream reader also names the denied tools; the count is unchanged.</summary>
public sealed class StreamJsonReaderDenialsTests
{
    [Fact]
    public void Accept_PermissionDenials_ToolNamesCollectedAndCountUnchanged()
    {
        // Arrange
        var reader = new StreamJsonReader(1024 * 1024);

        // Act
        reader.Accept("""{"type":"result","subtype":"success","is_error":false,"num_turns":3,"result":"x","total_cost_usd":0.1,"permission_denials":[{"tool_name":"mcp__m365__send-shared-mailbox-mail","tool_use_id":"a"},{"tool_name":"WebFetch"},{"tool_use_id":"no name"}]}""");

        // Assert
        reader.PermissionDenials.Should().Be(3);
        reader.PermissionDenialTools.Should().Equal("mcp__m365__send-shared-mailbox-mail", "WebFetch");
    }
}
