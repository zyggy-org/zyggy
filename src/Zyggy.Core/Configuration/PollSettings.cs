using Zyggy.Core.Bus;

namespace Zyggy.Core.Configuration;

/// <summary>The <c>poll</c> section of <c>node.json</c> (founding spec §5 item 3): the two intervals a machine may change; everything else in <see cref="PollOptions"/> is protocol.</summary>
/// <param name="ActiveSeconds">The active interval in seconds (10).</param>
/// <param name="IdleSeconds">The idle interval in seconds (60).</param>
public sealed record PollSettings(int ActiveSeconds = 10, int IdleSeconds = 60)
{
    /// <summary>The poller options: the spec's defaults with these two intervals.</summary>
    /// <returns>The options.</returns>
    public PollOptions ToPollOptions() => PollOptions.Default with
    {
        ActiveInterval = TimeSpan.FromSeconds(ActiveSeconds),
        IdleInterval = TimeSpan.FromSeconds(IdleSeconds),
    };
}
