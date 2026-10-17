using Zyggy.Core.Bus;

namespace Zyggy.Core.Configuration;

/// <summary>The <c>poll</c> section of <c>node.json</c> (founding spec §5 items 3 and 6): the two intervals and the safety-fetch period a machine may change; everything else in <see cref="PollOptions"/> is protocol.</summary>
/// <param name="ActiveSeconds">The active interval in seconds (10).</param>
/// <param name="IdleSeconds">The idle interval in seconds (60).</param>
/// <param name="SafetyFetchMinutes">The period of the unconditional fetch that catches a head check that lies (15).</param>
public sealed record PollSettings(int ActiveSeconds = 10, int IdleSeconds = 60, int SafetyFetchMinutes = 15)
{
    /// <summary>The poller options: the spec's defaults with these intervals.</summary>
    /// <returns>The options.</returns>
    public PollOptions ToPollOptions() => PollOptions.Default with
    {
        ActiveInterval = TimeSpan.FromSeconds(ActiveSeconds),
        IdleInterval = TimeSpan.FromSeconds(IdleSeconds),
        SafetyFetchInterval = TimeSpan.FromMinutes(SafetyFetchMinutes),
    };
}
