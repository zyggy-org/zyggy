namespace Zyggy.Core.Bus;

/// <summary>
/// How often the node service on Central sweeps (spec 17): <c>sweep.intervalMinutes</c> of <c>node.json</c>, unless a test overrides it.
/// </summary>
internal sealed class SweepScheduleOptions
{
    /// <summary>Gets or sets an interval that replaces <c>sweep.intervalMinutes</c>. Tests only; production leaves it <see langword="null"/>.</summary>
    public TimeSpan? IntervalOverride { get; set; }
}
