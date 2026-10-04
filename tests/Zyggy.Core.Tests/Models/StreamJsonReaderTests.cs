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
}
