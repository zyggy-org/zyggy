using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Bus;

namespace Zyggy.Core.Tests.Bus;

/// <summary>Spec 04 AC-3..AC-7: the poller decides when to wake without any I/O.</summary>
public sealed class PollStateTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);

    private static readonly PollOptions Options = PollOptions.Default;

    private static PollState Initial => PollState.Initial(T);

    [Theory]
    [InlineData(-1.0, 8)]
    [InlineData(0.0, 10)]
    [InlineData(1.0, 12)]
    public void Apply_UnchangedActive_NextDueIsTenSecondsPlusJitter(double jitterUnit, int expectedSeconds)
    {
        // Act
        PollTransition t = Initial.Apply(new Unchanged(4999), PollMode.Active, T, jitterUnit, Options);

        // Assert
        t.State.NextDueUtc.Should().Be(T.AddSeconds(expectedSeconds));
        t.Pull.Should().BeFalse();
        t.PhaseChanged.Should().BeNull();
        t.State.Phase.Should().Be(PollPhase.Polling);
        t.State.BackoffUntilUtc.Should().BeNull();
    }

    [Fact]
    public void Apply_UnchangedIdle_BaseIsSixtySeconds()
    {
        // Act
        PollTransition t = Initial.Apply(new Unchanged(null), PollMode.Idle, T, 0, Options);

        // Assert
        t.State.NextDueUtc.Should().Be(T.AddSeconds(60));
    }

    [Fact]
    public void Apply_UnchangedRepeated_KeepsETagAndPhase()
    {
        // Arrange
        PollState withETag = Initial.Apply(new Changed("W/\"abc\"", 4999), PollMode.Active, T, 0, Options).State;

        // Act
        PollTransition t1 = withETag.Apply(new Unchanged(4998), PollMode.Active, T.AddSeconds(10), 0, Options);
        PollTransition t2 = t1.State.Apply(new Unchanged(4997), PollMode.Active, T.AddSeconds(20), 0, Options);

        // Assert
        t2.State.ETag.Should().Be("W/\"abc\"");
        t2.State.Phase.Should().Be(PollPhase.Polling);
        t1.PhaseChanged.Should().BeNull();
        t2.PhaseChanged.Should().BeNull();
        t2.State.RateLimitRemaining.Should().Be(4997);
    }

    [Fact]
    public void Apply_Changed_StoresETagPullsAndKeepsInterval()
    {
        // Act
        PollTransition t = Initial.Apply(new Changed("W/\"abc\"", 4999), PollMode.Active, T, 0, Options);

        // Assert
        t.State.ETag.Should().Be("W/\"abc\"");
        t.Pull.Should().BeTrue();
        t.State.RateLimitRemaining.Should().Be(4999);
        t.State.NextDueUtc.Should().Be(T.AddSeconds(10));
        t.PhaseChanged.Should().BeNull();
    }

    [Fact]
    public void Apply_UnchangedAfterChanged_KeepsETag()
    {
        // Arrange
        PollState changed = Initial.Apply(new Changed("W/\"abc\"", 4999), PollMode.Active, T, 0, Options).State;

        // Act
        PollTransition t = changed.Apply(new Unchanged(null), PollMode.Active, T.AddSeconds(10), 0, Options);

        // Assert
        t.State.ETag.Should().Be("W/\"abc\"");
        t.Pull.Should().BeFalse();
    }

    [Fact]
    public void Apply_ChangedEmptyETag_StoresEmpty()
    {
        // Act: a 200 without a validator
        PollTransition t = Initial.Apply(new Changed(string.Empty, null), PollMode.Active, T, 0, Options);

        // Assert
        t.State.ETag.Should().BeEmpty();
        t.Pull.Should().BeTrue();
    }

    [Fact]
    public void Apply_ThrottledWithResetSevenMinutes_BackoffIsReset()
    {
        // Act
        PollTransition t = Initial.Apply(new Throttled(T.AddMinutes(7), null, 0), PollMode.Active, T, 0, Options);

        // Assert
        t.State.Phase.Should().Be(PollPhase.Throttled);
        t.State.BackoffUntilUtc.Should().Be(T.AddMinutes(7));
        t.State.NextDueUtc.Should().Be(T.AddMinutes(7));
        t.Pull.Should().BeFalse();
        t.PhaseChanged.Should().Be(PollPhase.Throttled);
    }

    [Fact]
    public void Apply_UnchangedRemainingBelowFloor_BackoffIsFloor()
    {
        // Act
        PollTransition t = Initial.Apply(new Unchanged(99), PollMode.Active, T, 0, Options);

        // Assert
        t.State.Phase.Should().Be(PollPhase.Throttled);
        t.State.BackoffUntilUtc.Should().Be(T.AddSeconds(120));
        t.State.NextDueUtc.Should().Be(T.AddSeconds(120));
        t.Pull.Should().BeFalse();
        t.PhaseChanged.Should().Be(PollPhase.Throttled);
    }

    [Fact]
    public void Apply_ChangedRemainingBelowFloor_StillPullsButThrottles()
    {
        // Act
        PollTransition t = Initial.Apply(new Changed("W/\"x\"", 50), PollMode.Active, T, 0, Options);

        // Assert
        t.Pull.Should().BeTrue();
        t.State.ETag.Should().Be("W/\"x\"");
        t.State.Phase.Should().Be(PollPhase.Throttled);
        t.State.BackoffUntilUtc.Should().Be(T.AddSeconds(120));
    }

    [Fact]
    public void Apply_ThrottledRetryAfterThirty_BackoffIsFloor()
    {
        // Act
        PollTransition t = Initial.Apply(new Throttled(null, TimeSpan.FromSeconds(30), null), PollMode.Active, T, 0, Options);

        // Assert
        t.State.BackoffUntilUtc.Should().Be(T.AddSeconds(120));
    }

    [Fact]
    public void Apply_ThrottledResetTenSeconds_BackoffIsFloor()
    {
        // Act
        PollTransition t = Initial.Apply(new Throttled(T.AddSeconds(10), null, null), PollMode.Active, T, 0, Options);

        // Assert
        t.State.BackoffUntilUtc.Should().Be(T.AddSeconds(120));
    }

    [Fact]
    public void Apply_ThrottledRetryAfterThreeHundred_RetryAfterWins()
    {
        // Act
        PollTransition t = Initial.Apply(new Throttled(null, TimeSpan.FromSeconds(300), null), PollMode.Active, T, 0, Options);

        // Assert
        t.State.BackoffUntilUtc.Should().Be(T.AddSeconds(300));
    }

    [Fact]
    public void Apply_ThrottledTwice_PhaseChangedOnlyOnce()
    {
        // Arrange
        PollTransition first = Initial.Apply(new Throttled(T.AddMinutes(7), null, 0), PollMode.Active, T, 0, Options);

        // Act
        PollTransition second = first.State.Apply(new Throttled(T.AddMinutes(9), null, 0), PollMode.Active, T.AddMinutes(7), 0, Options);

        // Assert
        first.PhaseChanged.Should().Be(PollPhase.Throttled);
        second.PhaseChanged.Should().BeNull();
        second.State.Phase.Should().Be(PollPhase.Throttled);
        second.State.BackoffUntilUtc.Should().Be(T.AddMinutes(9));
    }

    [Fact]
    public void Apply_UnchangedAfterBackoffElapsed_ReturnsToPollingWithPhaseChanged()
    {
        // Arrange
        PollState throttled = Initial.Apply(new Throttled(T.AddMinutes(7), null, 0), PollMode.Active, T, 0, Options).State;

        // Act
        PollTransition t = throttled.Apply(new Unchanged(4500), PollMode.Active, T.AddMinutes(7), 0, Options);

        // Assert
        t.State.Phase.Should().Be(PollPhase.Polling);
        t.PhaseChanged.Should().Be(PollPhase.Polling);
        t.State.BackoffUntilUtc.Should().BeNull();
        t.State.NextDueUtc.Should().Be(T.AddMinutes(7).AddSeconds(10));
    }

    [Fact]
    public void Apply_UnchangedWithMinimumIntervalThirty_NextDueIsThirtyPlusJitter()
    {
        // Act: x-poll-interval honoured (ex-OQ6)
        PollTransition t = Initial.Apply(new Unchanged(4000) { MinimumInterval = TimeSpan.FromSeconds(30) }, PollMode.Active, T, 1, Options);

        // Assert
        t.State.NextDueUtc.Should().Be(T.AddSeconds(32));
    }

    [Fact]
    public void Apply_UnchangedWithMinimumIntervalBelowBase_KeepsBase()
    {
        // Act
        PollTransition t = Initial.Apply(new Unchanged(4000) { MinimumInterval = TimeSpan.FromSeconds(3) }, PollMode.Active, T, 0, Options);

        // Assert
        t.State.NextDueUtc.Should().Be(T.AddSeconds(10));
    }

    [Fact]
    public void Apply_NextDueEqualsBackoffUntilPlusJitter()
    {
        // Act
        PollTransition t = Initial.Apply(new Throttled(T.AddMinutes(7), null, 0), PollMode.Active, T, -1, Options);

        // Assert
        t.State.NextDueUtc.Should().Be(t.State.BackoffUntilUtc!.Value.AddSeconds(-2));
    }

    [Fact]
    public void Apply_UnreachableSevenTimes_DelaysDoubleToCap()
    {
        // Arrange
        int[] expected = [10, 20, 40, 80, 160, 300, 300];
        PollState state = Initial;
        var clock = new FakeTimeProvider(T);
        var delays = new List<int>();
        var phaseChanges = new List<PollPhase?>();
        var failures = new List<int>();

        // Act
        foreach (int _ in expected)
        {
            DateTimeOffset now = clock.GetUtcNow();
            PollTransition t = state.Apply(new Unreachable("timeout"), PollMode.Active, now, 0, Options);
            delays.Add((int)(t.State.BackoffUntilUtc!.Value - now).TotalSeconds);
            phaseChanges.Add(t.PhaseChanged);
            failures.Add(t.State.ConsecutiveFailures);
            t.State.NextDueUtc.Should().Be(t.State.BackoffUntilUtc);
            t.Pull.Should().BeFalse();
            t.State.Phase.Should().Be(PollPhase.NetworkBackoff);
            state = t.State;
            clock.SetUtcNow(t.State.NextDueUtc);
        }

        // Assert
        delays.Should().Equal(expected);
        failures.Should().Equal(1, 2, 3, 4, 5, 6, 7);
        phaseChanges.Should().Equal(PollPhase.NetworkBackoff, null, null, null, null, null, null);
    }

    [Fact]
    public void Apply_UnchangedAfterNetworkBackoff_ResetsFailuresWithPhaseChangedPolling()
    {
        // Arrange
        PollState backedOff = Initial.Apply(new Unreachable("x"), PollMode.Active, T, 0, Options).State;

        // Act
        PollTransition t = backedOff.Apply(new Unchanged(null), PollMode.Active, T.AddSeconds(10), 0, Options);

        // Assert
        t.State.Phase.Should().Be(PollPhase.Polling);
        t.State.ConsecutiveFailures.Should().Be(0);
        t.PhaseChanged.Should().Be(PollPhase.Polling);
        t.State.BackoffUntilUtc.Should().BeNull();
    }

    [Fact]
    public void Apply_ChangedAfterNetworkBackoff_PullsAndResets()
    {
        // Arrange
        PollState backedOff = Initial.Apply(new Unreachable("x"), PollMode.Active, T, 0, Options).State;

        // Act
        PollTransition t = backedOff.Apply(new Changed("W/\"n\"", 4000), PollMode.Active, T.AddSeconds(10), 0, Options);

        // Assert
        t.Pull.Should().BeTrue();
        t.State.ConsecutiveFailures.Should().Be(0);
        t.State.ETag.Should().Be("W/\"n\"");
        t.PhaseChanged.Should().Be(PollPhase.Polling);
    }

    [Fact]
    public void Apply_UnreachableKeepsETag()
    {
        // Arrange
        PollState withETag = Initial.Apply(new Changed("W/\"abc\"", 4999), PollMode.Active, T, 0, Options).State;

        // Act
        PollTransition t = withETag.Apply(new Unreachable("x"), PollMode.Active, T.AddSeconds(10), 0, Options);

        // Assert
        t.State.ETag.Should().Be("W/\"abc\"");
        t.State.RateLimitRemaining.Should().Be(4999);
    }

    [Fact]
    public void Apply_Any_LeavesInputStateUnchanged()
    {
        // Arrange
        PollState before = Initial.Apply(new Changed("W/\"abc\"", 4999), PollMode.Active, T, 0, Options).State;
        PollState copy = before with { };

        // Act
        before.Apply(new Unreachable("x"), PollMode.Active, T.AddSeconds(10), 0.5, Options);
        before.Apply(new Throttled(T.AddMinutes(5), null, 0), PollMode.Idle, T.AddSeconds(10), -0.5, Options);
        before.Apply(new Unchanged(10), PollMode.Idle, T.AddSeconds(10), 0, Options);

        // Assert
        before.Should().Be(copy);
    }

    [Theory]
    [InlineData(-1.01)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    public void Apply_JitterUnitOutOfRange_Throws(double jitterUnit)
    {
        // Act
        Action act = () => Initial.Apply(new Unchanged(null), PollMode.Active, T, jitterUnit, Options);

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Initial_DueImmediatelyPolling()
    {
        // Act
        PollState state = PollState.Initial(T);

        // Assert
        state.Should().Be(new PollState(null, PollPhase.Polling, T, null, 0, null));
    }

    [Fact]
    public void Default_MatchesSpecTable()
    {
        // Assert: §5 as amended by A5
        Options.Should().Be(new PollOptions(
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(120),
            100,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(15)));
    }

    [Fact]
    public void Default_SafetyFetchInterval_FifteenMinutes()
    {
        // Assert: §5 item 6 (spec 07)
        Options.SafetyFetchInterval.Should().Be(TimeSpan.FromMinutes(15));
    }

    [Fact]
    public void SafetyFetchInterval_Zero_Throws()
    {
        // Act
        Action act = () => _ = new PollOptions(
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(120),
            100,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMinutes(5),
            TimeSpan.Zero);

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0, 60, 2, 120, 100, 10, 300)]
    [InlineData(10, 0, 2, 120, 100, 10, 300)]
    [InlineData(10, 60, -1, 120, 100, 10, 300)]
    [InlineData(10, 60, 2, 0, 100, 10, 300)]
    [InlineData(10, 60, 2, 120, -1, 10, 300)]
    [InlineData(10, 60, 2, 120, 100, 0, 300)]
    [InlineData(10, 60, 2, 120, 100, 10, 5)]
    public void Options_Invalid_Throws(int active, int idle, int jitter, int throttle, int floor, int min, int max)
    {
        // Act
        Action act = () => _ = new PollOptions(
            TimeSpan.FromSeconds(active),
            TimeSpan.FromSeconds(idle),
            TimeSpan.FromSeconds(jitter),
            TimeSpan.FromSeconds(throttle),
            floor,
            TimeSpan.FromSeconds(min),
            TimeSpan.FromSeconds(max),
            TimeSpan.FromMinutes(15));

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
