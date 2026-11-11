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
    DiscoveryDetail EffectiveDetail);
