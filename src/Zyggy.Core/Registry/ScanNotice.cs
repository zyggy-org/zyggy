namespace Zyggy.Core.Registry;

/// <summary>What a discovery scan noticed but did not publish (spec 14, Failure modes).</summary>
public enum ScanNoticeKind
{
    /// <summary>A <c>devRoots</c> entry does not exist.</summary>
    RootMissing,

    /// <summary>A <c>devRoots</c> entry could not be listed.</summary>
    RootUnreadable,

    /// <summary>Two folders of the machine derive the same name; neither is published.</summary>
    DuplicateName,

    /// <summary>A folder name derives no usable label.</summary>
    NameUnusable,

    /// <summary>More projects than <see cref="DiscoveryOptions.MaxProjects"/>; the rest are left out.</summary>
    ProjectCapReached,

    /// <summary>More agents or skills than <see cref="DiscoveryOptions.MaxEntriesPerProject"/>; the rest are left out.</summary>
    EntryCapReached,

    /// <summary>A description or summary matched a secret pattern and was not published.</summary>
    SecretRedacted,

    /// <summary>No secret patterns are available; the scan ran with <see cref="DiscoveryDetail.Names"/>.</summary>
    PatternsUnavailable,

    /// <summary>The scan took longer than <see cref="DiscoveryOptions.SlowScanThreshold"/>.</summary>
    SlowScan,
}

/// <summary>One notice of a scan.</summary>
/// <param name="Kind">The kind.</param>
/// <param name="Subject">A root, a folder path, a project name or a pattern name — never file content.</param>
/// <param name="Detail">A short detail (paths, counts, a pattern name), or <see langword="null"/>; never file content.</param>
public sealed record ScanNotice(ScanNoticeKind Kind, string Subject, string? Detail);
