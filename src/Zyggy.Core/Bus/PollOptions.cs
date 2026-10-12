namespace Zyggy.Core.Bus;

/// <summary>
/// The poller's intervals and thresholds (founding spec §5 items 3–5, as amended by deliverable 04). Every span must be
/// positive and the floor non-negative; <see cref="Default"/> is the spec's table.
/// </summary>
/// <param name="ActiveInterval">The base interval in <see cref="PollMode.Active"/> (10 s).</param>
/// <param name="IdleInterval">The base interval in <see cref="PollMode.Idle"/> (60 s).</param>
/// <param name="Jitter">The maximum jitter added to or taken from an interval (2 s).</param>
/// <param name="ThrottleInterval">The shortest back-off after a throttle (120 s).</param>
/// <param name="RateLimitFloor">Below this remaining budget the poller throttles itself (100).</param>
/// <param name="NetworkBackoffMin">The first back-off after a failed check (10 s); it doubles per failure.</param>
/// <param name="NetworkBackoffMax">The cap of the network back-off (5 min).</param>
public sealed record PollOptions(
    TimeSpan ActiveInterval,
    TimeSpan IdleInterval,
    TimeSpan Jitter,
    TimeSpan ThrottleInterval,
    int RateLimitFloor,
    TimeSpan NetworkBackoffMin,
    TimeSpan NetworkBackoffMax)
{
    /// <summary>Gets the §5 defaults: 10 s / 60 s, ±2 s, 120 s floor, budget floor 100, 10 s doubling to 5 min.</summary>
    public static PollOptions Default { get; } = new(
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(120),
        100,
        TimeSpan.FromSeconds(10),
        TimeSpan.FromMinutes(5));

    /// <summary>Gets the base interval in <see cref="PollMode.Active"/>.</summary>
    public TimeSpan ActiveInterval { get; init; } = Positive(ActiveInterval, nameof(ActiveInterval));

    /// <summary>Gets the base interval in <see cref="PollMode.Idle"/>.</summary>
    public TimeSpan IdleInterval { get; init; } = Positive(IdleInterval, nameof(IdleInterval));

    /// <summary>Gets the maximum jitter.</summary>
    public TimeSpan Jitter { get; init; } = NonNegative(Jitter, nameof(Jitter));

    /// <summary>Gets the shortest back-off after a throttle.</summary>
    public TimeSpan ThrottleInterval { get; init; } = Positive(ThrottleInterval, nameof(ThrottleInterval));

    /// <summary>Gets the remaining-budget floor.</summary>
    public int RateLimitFloor { get; init; } = RateLimitFloor >= 0 ? RateLimitFloor : throw new ArgumentOutOfRangeException(nameof(RateLimitFloor), RateLimitFloor, "The rate-limit floor must be non-negative.");

    /// <summary>Gets the first network back-off.</summary>
    public TimeSpan NetworkBackoffMin { get; init; } = Positive(NetworkBackoffMin, nameof(NetworkBackoffMin));

    /// <summary>Gets the network back-off cap; at least <see cref="NetworkBackoffMin"/>.</summary>
    public TimeSpan NetworkBackoffMax { get; init; } = NetworkBackoffMax >= NetworkBackoffMin
        ? NetworkBackoffMax
        : throw new ArgumentOutOfRangeException(nameof(NetworkBackoffMax), NetworkBackoffMax, "The network back-off cap must not be shorter than its minimum.");

    private static TimeSpan Positive(TimeSpan value, string name) =>
        value > TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(name, value, "The interval must be positive.");

    private static TimeSpan NonNegative(TimeSpan value, string name) =>
        value >= TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(name, value, "The jitter must be non-negative.");
}
