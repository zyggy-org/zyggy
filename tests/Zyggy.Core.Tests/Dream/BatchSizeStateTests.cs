using Zyggy.Core.Dream;

namespace Zyggy.Core.Tests.Dream;

/// <summary>AC-12: halve on a batch-attributable failure down to the minimum, restore on success, quarantine a stuck head.</summary>
public sealed class BatchSizeStateTests
{
    private static readonly DreamOptions Options = new();

    [Fact]
    public void OnAttributableFailure_HalvesDownToMin()
    {
        // Arrange
        var state = BatchSizeState.Initial(Options);
        var sizes = new List<int>();

        // Act
        for (var i = 0; i < 6; i++)
        {
            state = state.OnAttributableFailure(Options, $"head{i}");
            sizes.Add(state.CurrentLines);
        }

        // Assert
        sizes.Should().Equal(75, 37, 18, 10, 10, 10);
    }

    [Fact]
    public void OnSucceeded_RestoresConfigured()
    {
        // Arrange
        var state = BatchSizeState.Initial(Options).OnAttributableFailure(Options, "h").OnAttributableFailure(Options, "h");

        // Act
        state = state.OnSucceeded(Options);

        // Assert
        state.CurrentLines.Should().Be(150);
        state.ConsecutiveFailures.Should().Be(0);
    }

    [Fact]
    public void ShouldQuarantine_AfterThreeAtMinWithSameHead_True()
    {
        // Arrange
        var state = new BatchSizeState(10, 0, null);

        // Act
        var once = state.OnAttributableFailure(Options, "head");
        var twice = once.OnAttributableFailure(Options, "head");
        var thrice = twice.OnAttributableFailure(Options, "head");

        // Assert
        once.ShouldQuarantine(Options).Should().BeFalse();
        twice.ShouldQuarantine(Options).Should().BeFalse();
        thrice.ShouldQuarantine(Options).Should().BeTrue();
    }

    [Fact]
    public void ShouldQuarantine_DifferentHead_ResetsCount()
    {
        // Arrange
        var state = new BatchSizeState(10, 0, null).OnAttributableFailure(Options, "a").OnAttributableFailure(Options, "a");

        // Act
        state = state.OnAttributableFailure(Options, "b");

        // Assert
        state.ConsecutiveFailures.Should().Be(1);
        state.FirstLineHash.Should().Be("b");
        state.ShouldQuarantine(Options).Should().BeFalse();
    }

    [Fact]
    public void Serialize_ThenParse_RoundTrips()
    {
        // Arrange
        var state = new BatchSizeState(18, 2, "abc");

        // Act
        var back = BatchSizeState.Parse(state.Serialize(), Options);

        // Assert
        back.Should().Be(state);
        BatchSizeState.Parse("garbage", Options).Should().Be(BatchSizeState.Initial(Options));
        BatchSizeState.Parse(null, Options).Should().Be(BatchSizeState.Initial(Options));
    }
}
