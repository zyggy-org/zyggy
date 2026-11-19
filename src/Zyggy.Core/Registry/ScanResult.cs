namespace Zyggy.Core.Registry;

/// <summary>The outcome of one discovery scan.</summary>
/// <param name="Projects">The published projects, ordered by name (ordinal).</param>
/// <param name="Notices">What was noticed and left out.</param>
/// <param name="Elapsed">How long the scan took on the injected clock.</param>
/// <param name="EffectiveDetail">The detail the scan ran with (<see cref="DiscoveryDetail.Names"/> when patterns were unavailable).</param>
public sealed record ScanResult(
    IReadOnlyList<RegistryProject> Projects,
    IReadOnlyList<ScanNotice> Notices,
    TimeSpan Elapsed,
    DiscoveryDetail EffectiveDetail)
{
    /// <summary>
    /// Gets the folder of each kept project, by registry name, in the operating system's form and real case, whatever the detail (spec 56).
    /// For the node's in-memory <see cref="RegistrySnapshot"/> only: never published, logged or reported (founding spec §8), so a
    /// <see cref="DiscoveryDetail.Names"/> node routes jobs by name while its registry file carries no path.
    /// </summary>
    public IReadOnlyDictionary<string, string> Folders { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}
