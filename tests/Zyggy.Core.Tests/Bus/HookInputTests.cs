using Zyggy.Core.Bus;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Bus;

/// <summary>Spec 12 Step 4: the <c>Stop</c> and <c>SessionEnd</c> hook JSON read as the template's <c>stop.sh</c> reads it.</summary>
public sealed class HookInputTests
{
    private static readonly ContextEnvelopeOptions Options = new();

    private static StopHookNote? Parse(string text) => StopHookInput.Parse(new StringReader(text), Options);

    [Fact]
    public void Parse_FixtureInput_SessionCwdAndCollapsedFirstLine()
    {
        // Act
        var note = Parse(HookJson.Fixture);

        // Assert
        note.Should().Be(new StopHookNote(HookJson.SessionId, "/tmp/project", HookJson.FixtureFirstLine));
    }

    [Fact]
    public void Parse_BlankLinesThenText_FirstNonEmptyLine()
    {
        // Act + Assert
        Parse(HookJson.Stop("\n  \n\t\nThe note\nmore"))!.FirstLine.Should().Be("The note");
    }

    [Fact]
    public void Parse_CrLfAndLeadingBareCr_NotNotes()
    {
        // Act + Assert
        Parse(HookJson.Stop("\r\n\r\nfirst\r\nsecond"))!.FirstLine.Should().Be("first");
    }

    [Fact]
    public void Parse_OnlyCodeFence_FenceLine()
    {
        // Act + Assert
        Parse(HookJson.Stop("```\n"))!.FirstLine.Should().Be("```");
    }

    [Theory]
    [InlineData(321, true)]
    [InlineData(240, false)]
    public void Parse_Over240Chars_CutWithEllipsis_Exactly240Kept(int length, bool cut)
    {
        // Arrange
        var text = new string('x', length);

        // Act
        var line = Parse(HookJson.Stop(text))!.FirstLine;

        // Assert
        if (cut)
        {
            line.Should().Be(new string('x', 239) + "…");
        }
        else
        {
            line.Should().Be(text);
        }
    }

    [Fact]
    public void Parse_StopHookActiveTrue_Null()
    {
        // Act + Assert
        Parse(HookJson.Stop("note", active: true)).Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \n\t \r\n")]
    public void Parse_EmptyOrWhitespaceMessage_Null(string message)
    {
        // Act + Assert
        Parse(HookJson.Stop(message)).Should().BeNull();
    }

    [Fact]
    public void Parse_MessageAbsent_Null()
    {
        // Act + Assert
        Parse(HookJson.Stop(null)).Should().BeNull();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("\"\"")]
    [InlineData("")]
    public void Parse_NotJsonOrNotAnObject_Null(string text)
    {
        // Act + Assert
        Parse(text).Should().BeNull();
    }

    [Fact]
    public void Parse_UnknownMembers_Ignored()
    {
        // Act
        var note = Parse("""{"session_id":"abc","cwd":"/x","stop_hook_active":false,"last_assistant_message":"hi","transcript_path":"/t","permission_mode":"auto","stop_reason":"end_turn","extra":{"deep":[1,2]}}""");

        // Assert
        note.Should().Be(new StopHookNote("abc", "/x", "hi"));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("")]
    [InlineData("129")]
    public void Parse_SessionIdWithPathCharacters_Null(string sessionId)
    {
        // Arrange: "129" stands for a 129-character id
        var id = sessionId == "129" ? new string('a', 129) : sessionId;

        // Act + Assert
        Parse(HookJson.Stop("note", sessionId: id)).Should().BeNull();
        Parse(HookJson.Stop("note", sessionId: null)).Should().BeNull();
    }

    [Fact]
    public void SessionId_SessionEndJson_Id()
    {
        // Act + Assert
        SessionEndInput.SessionId(new StringReader(HookJson.SessionEnd())).Should().Be(HookJson.SessionId);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"session_id\":\"../x\"}")]
    public void SessionId_UnparseableOrAbsent_Null(string text)
    {
        // Act + Assert
        SessionEndInput.SessionId(new StringReader(text)).Should().BeNull();
    }
}
