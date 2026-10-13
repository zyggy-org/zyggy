namespace Zyggy.Core.Bus;

/// <summary>What the start-up check of the bus checkout found (founding spec §5 "Git operations": a dirty tree is stashed, never reset).</summary>
public enum BusStartupResult
{
    /// <summary>The checkout is on the bus branch with a clean tree.</summary>
    Clean,

    /// <summary>The tree was dirty; the changes were parked under a named stash and one warning was logged.</summary>
    Stashed,

    /// <summary>The checkout is on another branch than the bus branch; nothing was touched.</summary>
    WrongBranch,

    /// <summary>The configured path is not a git repository.</summary>
    NotARepository,

    /// <summary>The checkout has no <c>tenants/&lt;tenant&gt;/</c> directory for this node's tenant (§10: never a default tenant).</summary>
    NoTenantDirectory,

    /// <summary>A rebase or merge is in progress; nothing was touched.</summary>
    RebaseInProgress,

    /// <summary>The signing key this node writes with is absent from its secret store (spec 04 Assumption A4): run <c>zyggy secret set</c> first.</summary>
    KeyMissing,
}
