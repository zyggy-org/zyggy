namespace Zyggy.Core.Bus;

/// <summary>
/// The folders of the bus repository layout (founding spec §4 "Repository layout"). Closed: a new folder is a protocol
/// change. <see cref="BusPaths"/> builds the paths; <see cref="BusPaths.TryParse"/> maps a path back to one of these.
/// </summary>
public enum BusFolder
{
    /// <summary><c>tenants/&lt;org&gt;/nodes/&lt;machine&gt;/jobs</c>: envelopes addressed to the machine, not yet claimed.</summary>
    Jobs,

    /// <summary><c>…/jobs/claimed</c>: jobs the machine has claimed (§4 state machine row 2).</summary>
    Claimed,

    /// <summary><c>…/jobs/rejected</c>: envelopes the machine refused (§4 "Signature").</summary>
    Rejected,

    /// <summary><c>…/reports</c>: reports written by the machine, addressed to the sender.</summary>
    Reports,

    /// <summary><c>…/context</c>: context envelopes written for the machine (nodes write Central's).</summary>
    Context,

    /// <summary><c>tenants/&lt;org&gt;/registry</c>: one <c>&lt;machine&gt;.yaml</c> per machine (§4 registry).</summary>
    Registry,

    /// <summary><c>tenants/&lt;org&gt;/archive/&lt;YYYY-MM&gt;</c>: envelopes moved out of the live folders by the sweep (§4).</summary>
    Archive,
}
