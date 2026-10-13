namespace Zyggy.Core.Bus;

/// <summary>How eagerly the poller rings the doorbell (founding spec §5 item 3). The host decides: the Node service applies work hours; the probe is always <see cref="Active"/>.</summary>
public enum PollMode
{
    /// <summary>Every 10 s (±2 s): a session is likely waiting.</summary>
    Active,

    /// <summary>Every 60 s (±2 s): outside work hours.</summary>
    Idle,
}
