namespace Zyggy.Core.Bus;

/// <summary>
/// An immutable snapshot of the listener for the health endpoint and <c>zyggy status</c> (founding spec §11; spec 07): swapped as a
/// whole on every change, so a reader on another thread never sees a torn state.
/// </summary>
/// <param name="Poll">The poller's state (ETag, phase, next due instant, back-off, rate-limit budget).</param>
/// <param name="LastPollUtc">When the doorbell was last rung; <see langword="null"/> before the first probe.</param>
/// <param name="LastPullUtc">When the checkout was last pulled successfully.</param>
/// <param name="LastReportUtc">When the last report was committed (pushed or deferred).</param>
/// <param name="BusHeadSha">The checkout's <c>HEAD</c> after the last pull.</param>
/// <param name="JobsRunning">How many admitted jobs are running (claimed, not yet reported).</param>
/// <param name="JobsClaimedByMe">How many files sit in this machine's <c>jobs/claimed/</c> after the last pull.</param>
/// <param name="Mode">The poll mode of the last tick.</param>
public sealed record ListenerStatus(
    PollState Poll,
    DateTimeOffset? LastPollUtc,
    DateTimeOffset? LastPullUtc,
    DateTimeOffset? LastReportUtc,
    string? BusHeadSha,
    int JobsRunning,
    int JobsClaimedByMe,
    PollMode Mode);
