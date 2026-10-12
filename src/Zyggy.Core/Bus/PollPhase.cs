namespace Zyggy.Core.Bus;

/// <summary>The poller's phase (founding spec §5 items 3–5). A change of phase is logged once (§11); a tick inside a phase is silent.</summary>
public enum PollPhase
{
    /// <summary>Checking at the mode's interval.</summary>
    Polling,

    /// <summary>Backing off because the host's request budget is low or it asked us to (§5 item 4).</summary>
    Throttled,

    /// <summary>Backing off exponentially after failed checks (§5 item 5).</summary>
    NetworkBackoff,
}
