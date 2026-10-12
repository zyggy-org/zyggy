namespace Zyggy.Core.Bus;

/// <summary>
/// What one ring of the doorbell told us (founding spec §5 poll loop items 1–4): the host's conditional head check mapped
/// to a closed set of outcomes, so the poller state machine never sees HTTP. <see cref="MinimumInterval"/> carries the
/// host's own pacing hint when it sent one (the provider maps the host's header to it).
/// </summary>
public abstract record HeadProbe
{
    private protected HeadProbe()
    {
    }

    /// <summary>Gets the shortest interval the host asked for before the next check, or <see langword="null"/> when it sent none.</summary>
    public TimeSpan? MinimumInterval { get; init; }
}

/// <summary>Nothing new on the branch (a <c>304</c>, or the same head marker).</summary>
/// <param name="RateLimitRemaining">The host's remaining request budget when it sent one.</param>
public sealed record Unchanged(int? RateLimitRemaining) : HeadProbe;

/// <summary>The branch moved: pull and process (a <c>200</c> with a new validator).</summary>
/// <param name="ETag">The new validator to send on the next check; empty when the host sent none.</param>
/// <param name="RateLimitRemaining">The host's remaining request budget when it sent one.</param>
public sealed record Changed(string ETag, int? RateLimitRemaining) : HeadProbe;

/// <summary>The host asked us to slow down (a <c>403</c> or <c>429</c>); §5 item 4 as amended by deliverable 04.</summary>
/// <param name="ResetUtc">When the budget resets, when the host said.</param>
/// <param name="RetryAfter">The delay the host asked for, when sent.</param>
/// <param name="RateLimitRemaining">The remaining budget, when sent.</param>
public sealed record Throttled(DateTimeOffset? ResetUtc, TimeSpan? RetryAfter, int? RateLimitRemaining) : HeadProbe;

/// <summary>The check could not be made: network, timeout, <c>401</c>, <c>5xx</c> or a missing token (§5 item 5).</summary>
/// <param name="Detail">A short reason — a status code, an exception type name or a fixed phrase; never a secret or header value.</param>
public sealed record Unreachable(string Detail) : HeadProbe;
