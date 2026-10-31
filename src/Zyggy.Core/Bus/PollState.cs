namespace Zyggy.Core.Bus;

/// <summary>
/// The poller as a pure state machine (founding spec §5 poll loop, §9 "the poller is a pure state machine"): no I/O, no
/// clock — the host passes <c>now</c>, the probe outcome and a jitter unit, and gets the next state plus what to do.
/// </summary>
/// <param name="ETag">The validator to send on the next check (<c>If-None-Match</c>), or <see langword="null"/> before the first <c>200</c>.</param>
/// <param name="Phase">The current phase.</param>
/// <param name="NextDueUtc">When the next check is due.</param>
/// <param name="BackoffUntilUtc">The end of the current back-off, or <see langword="null"/> while polling.</param>
/// <param name="ConsecutiveFailures">How many checks in a row were <see cref="Unreachable"/>.</param>
/// <param name="RateLimitRemaining">The host's last reported remaining budget, when known.</param>
public sealed record PollState(
    string? ETag,
    PollPhase Phase,
    DateTimeOffset NextDueUtc,
    DateTimeOffset? BackoffUntilUtc,
    int ConsecutiveFailures,
    int? RateLimitRemaining)
{
    /// <summary>The state before the first check: due immediately, polling, no validator.</summary>
    /// <param name="now">The current instant.</param>
    /// <returns>The initial state.</returns>
    public static PollState Initial(DateTimeOffset now) => new(null, PollPhase.Polling, now, null, 0, null);

    /// <summary>
    /// Applies one probe outcome (§5 items 3–5, first matching rule wins): a quiet or changed branch schedules the next check
    /// at the mode's interval (raised to the host's minimum) plus jitter; a low budget or an explicit throttle backs off until
    /// the later of the throttle floor, the reset time and <c>retry-after</c>; a failed check backs off exponentially from the
    /// minimum to the cap.
    /// </summary>
    /// <param name="outcome">What the doorbell told us.</param>
    /// <param name="mode">The host's current mode.</param>
    /// <param name="now">The current instant; the only time source.</param>
    /// <param name="jitterUnit">A number in [−1, 1] from the host's random source; multiplied by <see cref="PollOptions.Jitter"/>, capped at half the interval on a quiet or changed branch (spec 13).</param>
    /// <param name="options">The intervals and thresholds.</param>
    /// <returns>The next state, whether to pull, and the new phase when it changed.</returns>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="jitterUnit"/> is outside [−1, 1].</exception>
    public PollTransition Apply(HeadProbe outcome, PollMode mode, DateTimeOffset now, double jitterUnit, PollOptions options)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(options);
        if (double.IsNaN(jitterUnit) || jitterUnit < -1 || jitterUnit > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(jitterUnit), jitterUnit, "The jitter unit must be in [-1, 1].");
        }

        TimeSpan jitter = options.Jitter * jitterUnit;
        PollState next = outcome switch
        {
            Unchanged unchanged => Quiet(unchanged.RateLimitRemaining, ETag, outcome.MinimumInterval, mode, now, jitterUnit, options),
            Changed changed => Quiet(changed.RateLimitRemaining, changed.ETag, outcome.MinimumInterval, mode, now, jitterUnit, options),
            Throttled throttled => Throttle(
                Later(now + options.ThrottleInterval, throttled.ResetUtc, throttled.RetryAfter is { } retry ? now + retry : null),
                throttled.RateLimitRemaining ?? RateLimitRemaining,
                jitter),
            Unreachable => BackOff(now, jitter, options),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown probe outcome."),
        };

        return new PollTransition(next, outcome is Changed, next.Phase == Phase ? null : next.Phase);
    }

    private PollState Quiet(int? remaining, string? etag, TimeSpan? minimumInterval, PollMode mode, DateTimeOffset now, double jitterUnit, PollOptions options)
    {
        if (remaining is { } r && r < options.RateLimitFloor)
        {
            return Throttle(now + options.ThrottleInterval, remaining, options.Jitter * jitterUnit) with { ETag = etag };
        }

        TimeSpan interval = mode == PollMode.Active ? options.ActiveInterval : options.IdleInterval;
        if (minimumInterval is { } minimum && minimum > interval)
        {
            interval = minimum;
        }

        // §5 item 3 as amended by A13-7: the jitter is at most half the interval, so 1 s stays 0.5–1.5 s; 10 s and 60 s are unchanged.
        TimeSpan jitter = (options.Jitter < interval / 2 ? options.Jitter : interval / 2) * jitterUnit;
        return new PollState(etag, PollPhase.Polling, now + interval + jitter, null, 0, remaining);
    }

    private PollState Throttle(DateTimeOffset until, int? remaining, TimeSpan jitter) =>
        new(ETag, PollPhase.Throttled, until + jitter, until, 0, remaining);

    private PollState BackOff(DateTimeOffset now, TimeSpan jitter, PollOptions options)
    {
        double seconds = Math.Min(options.NetworkBackoffMax.TotalSeconds, options.NetworkBackoffMin.TotalSeconds * Math.Pow(2, ConsecutiveFailures));
        DateTimeOffset until = now + TimeSpan.FromSeconds(seconds);
        return new PollState(ETag, PollPhase.NetworkBackoff, until + jitter, until, ConsecutiveFailures + 1, RateLimitRemaining);
    }

    private static DateTimeOffset Later(DateTimeOffset first, DateTimeOffset? second, DateTimeOffset? third)
    {
        DateTimeOffset result = first;
        if (second is { } s && s > result)
        {
            result = s;
        }

        if (third is { } t && t > result)
        {
            result = t;
        }

        return result;
    }
}
