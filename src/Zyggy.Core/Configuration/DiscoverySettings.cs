using Zyggy.Core.Registry;

namespace Zyggy.Core.Configuration;

/// <summary>
/// The <c>discovery</c> section of <c>node.json</c> (founding spec §10 as amended by A14-5): how often the node scans its <c>devRoots</c>,
/// how often an unchanged registry file is refreshed, and how much it carries. The defaults apply when the section is absent. Never a secret.
/// </summary>
/// <param name="IntervalMinutes">Minutes between two scans of the node service, 1–1440.</param>
/// <param name="LastSeenHours">Hours after which an unchanged registry file is committed again, 1–168.</param>
/// <param name="Detail">How much the registry file carries; <see cref="DiscoveryDetail.Names"/> on the work laptop (§8).</param>
/// <param name="Depth">Folder levels below each root that are searched, 1–6 (spec 45; root children are level 1).</param>
public sealed record DiscoverySettings(int IntervalMinutes = 60, int LastSeenHours = 6, DiscoveryDetail Detail = DiscoveryDetail.Full, int Depth = 2)
{
    /// <summary>
    /// Gets the folder-name globs (<c>*</c>, <c>?</c>) never descended into, added to the built-in list (spec 45, <c>discovery.exclude</c>);
    /// empty when absent.
    /// </summary>
    public IReadOnlyList<string> Exclude { get; init; } = [];

    /// <summary>Gets how a project's name is derived (spec 45, <c>discovery.naming</c>); <see cref="DiscoveryNaming.Leaf"/> when absent.</summary>
    public DiscoveryNaming Naming { get; init; } = DiscoveryNaming.Leaf;
}
