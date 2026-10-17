using Zyggy.Core.Models;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Models;

public sealed class StreamJsonReaderTests
{
    [Fact]
    public void Accept_ResultSuccess_ReadsTotalCostTurnsDurationTokensStructuredOutputAndModelFromInit()
    {
        // Arrange
        var reader = new StreamJsonReader(1024 * 1024);

        // Act
        reader.Accept(StreamLines.Init("fake-model-1"));
        reader.Accept(StreamLines.Assistant());
        reader.Accept(StreamLines.Result(structuredOutputJson: """{"ok":true}"""));

        // Assert
        reader.SawResult.Should().BeTrue();
        reader.ResultUnparseable.Should().BeFalse();
        reader.Model.Should().Be("fake-model-1");
        reader.CostUsd.Should().Be(0.0123m);
        reader.NumTurns.Should().Be(2);
        reader.DurationMs.Should().Be(1500);
        reader.InputTokens.Should().Be(100);
        reader.OutputTokens.Should().Be(20);
        reader.IsError.Should().BeFalse();
        reader.Subtype.Should().Be("success");
        reader.ResultText.Should().Be("done");
        reader.StructuredOutput!.Value.GetProperty("ok").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Accept_CostUsdOnly_LeavesCostNull()
    {
        // Arrange
        var reader = new StreamJsonReader(1024 * 1024);

        // Act
        reader.Accept(StreamLines.Result(withTotalCost: false, withLegacyCost: true));

        // Assert
        reader.SawResult.Should().BeTrue();
        reader.CostUsd.Should().BeNull();
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("[1,2,3]")]
    public void Accept_NonJsonLine_IsIgnored(string line)
    {
        // Arrange
        var reader = new StreamJsonReader(1024 * 1024);

        // Act
        reader.Accept(line);
        reader.Accept(StreamLines.Result());

        // Assert
        reader.SawResult.Should().BeTrue();
        reader.ResultUnparseable.Should().BeFalse();
    }

    [Theory]
    [InlineData("""{"type":"result","subtype":"success","total_cost_usd": oops""")]
    [InlineData("""{"type":"result","subtype":"success","is_error":"maybe"}""")]
    [InlineData("""{"type":"result","subtype":"success","is_error":false,"total_cost_usd":"cheap"}""")]
    public void Accept_ResultLineUnparseable_MarksUnparseable(string line)
    {
        // Arrange
        var reader = new StreamJsonReader(1024 * 1024);

        // Act
        reader.Accept(line);

        // Assert
        reader.ResultUnparseable.Should().BeTrue();
    }

    [Fact]
    public void Accept_BytesBeyondCap_MarksOutputTooLarge()
    {
        // Arrange
        var reader = new StreamJsonReader(64);

        // Act
        reader.Accept(StreamLines.Init());
        reader.Accept(StreamLines.Result());

        // Assert
        reader.OutputTooLarge.Should().BeTrue();
    }

    [Fact]
    public void Accept_PermissionDenials_CountsThem()
    {
        // Arrange
        var reader = new StreamJsonReader(1024 * 1024);

        // Act
        reader.Accept(StreamLines.Result(permissionDenials: 3));

        // Assert
        reader.PermissionDenials.Should().Be(3);
    }

    [Fact]
    public void Accept_TwoAssistantMessages_KeepsOnlyTheLastText()
    {
        // Arrange
        var reader = new StreamJsonReader(1024 * 1024);

        // Act
        reader.Accept(StreamLines.Assistant("first"));
        reader.Accept(StreamLines.Assistant("second"));

        // Assert
        reader.LastAssistantText.Should().Be("second");
    }

    [Fact]
    public void Accept_AssistantWithTwoTextBlocks_Concatenated()
    {
        // Arrange
        var reader = new StreamJsonReader(1024 * 1024);
        const string Line = """{"type":"assistant","message":{"content":[{"type":"text","text":"one"},{"type":"tool_use","name":"Read"},{"type":"text","text":"two"}]}}""";

        // Act
        reader.Accept(Line);

        // Assert
        reader.LastAssistantText.Should().Be("one\ntwo");
    }

    [Fact]
    public void Accept_AssistantToolUseOnly_LastTextUnchanged()
    {
        // Arrange
        var reader = new StreamJsonReader(1024 * 1024);
        reader.Accept(StreamLines.Assistant("kept"));

        // Act
        reader.Accept("""{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Read","input":{}}]}}""");

        // Assert
        reader.LastAssistantText.Should().Be("kept");
    }

    [Fact]
    public void Accept_AssistantTextOver2000_TruncatedHead()
    {
        // Arrange
        var reader = new StreamJsonReader(1024 * 1024);
        var text = new string('a', 1500) + new string('b', 1500);

        // Act
        reader.Accept(StreamLines.Assistant(text));

        // Assert
        reader.LastAssistantText.Should().Be(text[..2000]);
    }

    [Fact]
    public void Accept_ThreeMegabytesThenResult_ResultParsedAndOutputTooLarge()
    {
        // Arrange
        var reader = new StreamJsonReader(2 * 1024 * 1024);
        var line = StreamLines.Assistant(new string('x', 10_000));

        // Act
        reader.Accept(StreamLines.Init());
        for (var written = 0L; written < 3L * 1024 * 1024; written += line.Length + 1)
        {
            reader.Accept(line);
        }

        reader.Accept(StreamLines.Result());

        // Assert
        reader.SawResult.Should().BeTrue();
        reader.CostUsd.Should().NotBeNull();
        reader.NumTurns.Should().NotBeNull();
        reader.OutputTooLarge.Should().BeTrue();
        reader.LastAssistantText!.Length.Should().BeLessThanOrEqualTo(2000);
    }
}
