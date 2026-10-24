using System.Globalization;

using Zyggy.Core.Bus;

namespace Zyggy.Core.Health;

/// <summary>The verdict <c>zyggy status</c> prints (spec 08 AC-3, ex-Q4): worst first.</summary>
public enum HealthLevel
{
    /// <summary>Polling, pulled, logged in, current: nothing to do.</summary>
    Green,

    /// <summary>Look later: back-off, an unprobed login, a low rate limit, never pulled, a version skew.</summary>
    Amber,

    /// <summary>Act now: not logged in, or not polling.</summary>
    Red,

    /// <summary>No node service answers on the health URL.</summary>
    Down,
}

/// <summary>A verdict with its reasons (in the spec's order) and notes (running and claimed jobs).</summary>
/// <param name="Level">The level.</param>
/// <param name="Reasons">Why it is not green; empty for green.</param>
/// <param name="Notes">Facts worth a line that change no level.</param>
public sealed record HealthVerdict(HealthLevel Level, IReadOnlyList<string> Reasons, IReadOnlyList<string> Notes);

/// <summary>
/// "Green" defined once, as a pure function over the fourteen §11 fields (spec 07 ex-Q9 left the verdict to the CLI; spec 08 ex-Q4 fixed
/// the rule): the Node adds no verdict field, the alerting deliverable reads the same rule. Reads no clock: the instant is an argument.
/// </summary>
public static class HealthRules
{
    /// <summary>How many idle intervals without a poll make a node red.</summary>
    public const int StalePollIntervals = 3;

    /// <summary>Evaluates <paramref name="health"/> at <paramref name="now"/>.</summary>
    /// <param name="health">The body, or <see langword="null"/> when nothing answered.</param>
    /// <param name="now">The instant of the evaluation.</param>
    /// <param name="poll">The poll options the thresholds come from (idle interval, rate-limit floor).</param>
    /// <param name="cliVersion">The CLI's own version, compared with the node's.</param>
    /// <param name="url">The health URL, for the <c>Down</c> reason.</param>
    /// <returns>The verdict.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="poll"/>, <paramref name="cliVersion"/> or <paramref name="url"/> is null.</exception>
    public static HealthVerdict Evaluate(NodeHealth? health, DateTimeOffset now, PollOptions poll, string cliVersion, string url)
    {
        ArgumentNullException.ThrowIfNull(poll);
        ArgumentNullException.ThrowIfNull(cliVersion);
        ArgumentNullException.ThrowIfNull(url);
        if (health is null)
        {
            return new HealthVerdict(HealthLevel.Down, ["no node service answering on " + url], []);
        }

        var level = HealthLevel.Green;
        var reasons = new List<string>();
        void Add(HealthLevel at, string reason)
        {
            reasons.Add(reason);
            if (at > level)
            {
                level = at;
            }
        }

        if (health.ClaudeAuthOk == false)
        {
            Add(HealthLevel.Red, "claude not logged in");
        }

        bool backingOff = health.BackoffUntilUtc is { } until && until > now;
        if (health.LastPollUtc is null)
        {
            Add(HealthLevel.Red, "never polled");
        }
        else if (now - health.LastPollUtc.Value > poll.IdleInterval * StalePollIntervals && !backingOff)
        {
            Add(HealthLevel.Red, "not polling: last poll " + Age(now, health.LastPollUtc.Value));
        }

        if (backingOff)
        {
            Add(HealthLevel.Amber, "backing off until " + health.BackoffUntilUtc!.Value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        }

        if (health.ClaudeAuthOk is null)
        {
            Add(HealthLevel.Amber, "claude auth not yet probed");
        }

        if (health.RateLimitRemaining is { } remaining && remaining < poll.RateLimitFloor)
        {
            Add(HealthLevel.Amber, "rate limit low: " + remaining.ToString(CultureInfo.InvariantCulture));
        }

        if (health.LastPullUtc is null)
        {
            Add(HealthLevel.Amber, "never pulled");
        }

        if (string.IsNullOrEmpty(health.Version))
        {
            Add(HealthLevel.Amber, "node version unknown");
        }
        else if (!string.Equals(health.Version, cliVersion, StringComparison.Ordinal))
        {
            Add(HealthLevel.Amber, "node " + health.Version + ", cli " + cliVersion);
        }

        var notes = new List<string>();
        if (health.JobsRunning > 0)
        {
            notes.Add(health.JobsRunning.ToString(CultureInfo.InvariantCulture) + " job(s) running");
        }

        if (health.JobsClaimedByMe > 0)
        {
            notes.Add(health.JobsClaimedByMe.ToString(CultureInfo.InvariantCulture) + " claimed");
        }

        return new HealthVerdict(level, reasons, notes);
    }

    /// <summary>An age in the coarsest unit that keeps two digits: <c>45 s ago</c>, <c>3 min ago</c>, <c>2 h ago</c>.</summary>
    /// <param name="now">The instant of the evaluation.</param>
    /// <param name="then">The earlier instant.</param>
    /// <returns>The text.</returns>
    public static string Age(DateTimeOffset now, DateTimeOffset then)
    {
        TimeSpan age = now - then;
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        if (age < TimeSpan.FromSeconds(120))
        {
            return ((long)age.TotalSeconds).ToString(CultureInfo.InvariantCulture) + " s ago";
        }

        if (age < TimeSpan.FromMinutes(120))
        {
            return ((long)age.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " min ago";
        }

        return ((long)age.TotalHours).ToString(CultureInfo.InvariantCulture) + " h ago";
    }
}
