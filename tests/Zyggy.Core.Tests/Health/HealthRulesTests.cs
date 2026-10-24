using Zyggy.Core.Bus;
using Zyggy.Core.Health;

namespace Zyggy.Core.Tests.Health;

/// <summary>Spec 08 AC-3: "green" is one pure function over the fourteen §11 fields; worst level wins, reasons in the spec's order, no clock but <c>now</c>.</summary>
public sealed class HealthRulesTests
{
    private const string Url = "http://localhost:4711/health";

    private const string CliVersion = "0.10.0";

    private static readonly DateTimeOffset Now = new(2026, 10, 24, 10, 0, 0, TimeSpan.Zero);

    private static NodeHealth Fresh => new(CliVersion, "acme", "home-laptop", Now.AddSeconds(-5), "\"e\"", Now.AddSeconds(-30), "0123", 0, 0, Now.AddMinutes(-10), 4999, null, true, null);

    public static TheoryData<string, NodeHealth?, HealthLevel, string?> Matrix => new()
    {
        { "down", null, HealthLevel.Down, "no node service answering on " + Url },
        { "green", Fresh, HealthLevel.Green, null },
        { "not polling", Fresh with { LastPollUtc = Now.AddSeconds(-181) }, HealthLevel.Red, "not polling: last poll 3 min ago" },
        { "backing off", Fresh with { LastPollUtc = Now.AddSeconds(-181), BackoffUntilUtc = Now.AddMinutes(4) }, HealthLevel.Amber, "backing off until 2026-10-24T10:04:00Z" },
        { "auth false", Fresh with { ClaudeAuthOk = false }, HealthLevel.Red, "claude not logged in" },
        { "auth null", Fresh with { ClaudeAuthOk = null }, HealthLevel.Amber, "claude auth not yet probed" },
        { "rate limit", Fresh with { RateLimitRemaining = 99 }, HealthLevel.Amber, "rate limit low: 99" },
        { "never pulled", Fresh with { LastPullUtc = null }, HealthLevel.Amber, "never pulled" },
        { "version skew", Fresh with { Version = "0.9.0" }, HealthLevel.Amber, "node 0.9.0, cli 0.10.0" },
        { "version unknown", Fresh with { Version = string.Empty }, HealthLevel.Amber, "node version unknown" },
        { "never polled", Fresh with { LastPollUtc = null }, HealthLevel.Red, "never polled" },
        { "idle 179 s", Fresh with { LastPollUtc = Now.AddSeconds(-179) }, HealthLevel.Green, null },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Evaluate_Matrix_LevelAndReasons(string scenario, NodeHealth? health, HealthLevel level, string? reason)
    {
        // Act
        HealthVerdict verdict = HealthRules.Evaluate(health, Now, PollOptions.Default, CliVersion, Url);

        // Assert
        verdict.Level.Should().Be(level, scenario);
        if (reason is null)
        {
            verdict.Reasons.Should().BeEmpty(scenario);
        }
        else
        {
            verdict.Reasons.Should().Equal([reason]);
        }
    }

    [Fact]
    public void Evaluate_JobsRunningAndClaimed_GreenWithNotes()
    {
        // Act
        HealthVerdict verdict = HealthRules.Evaluate(Fresh with { JobsRunning = 2, JobsClaimedByMe = 3 }, Now, PollOptions.Default, CliVersion, Url);

        // Assert
        verdict.Level.Should().Be(HealthLevel.Green);
        verdict.Notes.Should().Equal("2 job(s) running", "3 claimed");
    }

    [Fact]
    public void Evaluate_SeveralReasons_WorstWinsAndOrderIsTheSpecs()
    {
        // Act
        HealthVerdict verdict = HealthRules.Evaluate(Fresh with { ClaudeAuthOk = false, LastPullUtc = null, Version = "0.9.0" }, Now, PollOptions.Default, CliVersion, Url);

        // Assert
        verdict.Level.Should().Be(HealthLevel.Red);
        verdict.Reasons.Should().Equal("claude not logged in", "never pulled", "node 0.9.0, cli 0.10.0");
    }

    [Fact]
    public void Evaluate_ReadsNoClock()
    {
        // Arrange: the same `now`, called at two different wall-clock moments
        HealthVerdict first = HealthRules.Evaluate(Fresh with { LastPollUtc = Now.AddSeconds(-100) }, Now, PollOptions.Default, CliVersion, Url);
        Thread.Sleep(20);

        // Act
        HealthVerdict second = HealthRules.Evaluate(Fresh with { LastPollUtc = Now.AddSeconds(-100) }, Now, PollOptions.Default, CliVersion, Url);

        // Assert
        second.Should().BeEquivalentTo(first);
        first.Level.Should().Be(HealthLevel.Green);
    }

    [Theory]
    [InlineData(45, "45 s ago")]
    [InlineData(180, "3 min ago")]
    [InlineData(7200, "2 h ago")]
    [InlineData(119, "119 s ago")]
    [InlineData(7199, "119 min ago")]
    public void Age_Formats(int seconds, string expected)
    {
        HealthRules.Age(Now, Now.AddSeconds(-seconds)).Should().Be(expected);
    }
}
