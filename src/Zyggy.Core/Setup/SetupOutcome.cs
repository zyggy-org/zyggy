namespace Zyggy.Core.Setup;

/// <summary>The outcome of one <see cref="SetupItem"/> (spec 42 Contracts).</summary>
public enum SetupOutcome
{
    /// <summary>Changed by this run.</summary>
    Done,

    /// <summary>Already as it should be; nothing written.</summary>
    AlreadyDone,

    /// <summary>Not changed on purpose (an owner value, a dirty checkout, an informational finding).</summary>
    LeftAsIs,

    /// <summary>A needed secret is not in the store; the detail names the command that sets it.</summary>
    Missing,

    /// <summary>Could not be done; the detail says why.</summary>
    Failed,
}
