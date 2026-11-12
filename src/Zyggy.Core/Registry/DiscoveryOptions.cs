namespace Zyggy.Core.Registry;

/// <summary>
/// The discovery settings (<c>node.json</c> <c>discovery</c>, founding spec §10 as amended by A14-5) plus the code-only caps.
/// </summary>
public sealed class DiscoveryOptions
{
    /// <summary>Gets or sets the minutes between two scans of the node service (1–1440).</summary>
    public int IntervalMinutes { get; set; } = 60;

    /// <summary>Gets or sets the hours after which an unchanged registry file is committed again to refresh <c>last_seen</c> (1–168).</summary>
    public int LastSeenHours { get; set; } = 6;

    /// <summary>Gets or sets how much the registry file carries.</summary>
    public DiscoveryDetail Detail { get; set; } = DiscoveryDetail.Full;

    /// <summary>Gets or sets the most projects one registry file lists.</summary>
    public int MaxProjects { get; set; } = 500;

    /// <summary>Gets or sets the most agents, and the most skills, one project lists.</summary>
    public int MaxEntriesPerProject { get; set; } = 100;

    /// <summary>Gets or sets the scan duration above which a <see cref="ScanNoticeKind.SlowScan"/> is noticed.</summary>
    public TimeSpan SlowScanThreshold { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets an interval that replaces <see cref="IntervalMinutes"/>. Tests only; production leaves it <see langword="null"/>.</summary>
    public TimeSpan? IntervalOverride { get; set; }

    /// <summary>Gets the interval between two scans.</summary>
    public TimeSpan Interval => IntervalOverride ?? TimeSpan.FromMinutes(IntervalMinutes);

    /// <summary>Checks the ranges.</summary>
    /// <returns>The first offending key (<c>intervalMinutes</c>, <c>lastSeenHours</c>), or <see langword="null"/> when valid.</returns>
    public string? Validate() =>
        IntervalMinutes is < 1 or > 1440 ? "intervalMinutes"
        : LastSeenHours is < 1 or > 168 ? "lastSeenHours"
        : null;
}
