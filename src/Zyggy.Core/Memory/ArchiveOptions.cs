namespace Zyggy.Core.Memory;

/// <summary>
/// The archive's caps and allow-list (spec 37 Configuration): code defaults, optionally tightened by <c>instance/archive.json</c>.
/// A value above its hard ceiling (or below its minimum) is a configuration error, never silently clamped.
/// </summary>
public sealed record ArchiveOptions
{
    /// <summary>The ceiling of <see cref="ItemMaxBytes"/> (25 MiB, owner decision OQ-2).</summary>
    public const long ItemCeiling = 26_214_400;

    /// <summary>The ceiling of <see cref="ProjectMaxBytes"/> (200 MiB).</summary>
    public const long ProjectCeiling = 209_715_200;

    /// <summary>The ceiling of <see cref="TotalMaxBytes"/> (500 MiB).</summary>
    public const long TotalCeiling = 524_288_000;

    /// <summary>Gets the media types an item may have; the instance may only remove from the built-in six.</summary>
    public IReadOnlySet<ArchiveMediaType> AllowedTypes { get; init; } = new HashSet<ArchiveMediaType>(Enum.GetValues<ArchiveMediaType>());

    /// <summary>Gets the largest item in bytes (10 MiB).</summary>
    public long ItemMaxBytes { get; init; } = 10_485_760;

    /// <summary>Gets the most bytes one project's archive directory may hold, items and sidecars (50 MiB).</summary>
    public long ProjectMaxBytes { get; init; } = 52_428_800;

    /// <summary>Gets the most bytes the whole archive may hold (200 MiB).</summary>
    public long TotalMaxBytes { get; init; } = 209_715_200;

    /// <summary>Gets the instance's extra denied source locations, absolute paths after <c>~</c> expansion.</summary>
    public IReadOnlyList<string> SourceDeny { get; init; } = [];

    /// <summary>Returns the <c>archive.json</c> keys whose value is above its ceiling or below its minimum.</summary>
    /// <returns>The offending keys in table order; empty when valid.</returns>
    public IReadOnlyList<string> Validate()
    {
        var offending = new List<string>();
        void Check(bool ok, string key)
        {
            if (!ok)
            {
                offending.Add(key);
            }
        }

        Check(ItemMaxBytes >= 1 && ItemMaxBytes <= ItemCeiling, "item_max_bytes");
        Check(ProjectMaxBytes >= ItemMaxBytes && ProjectMaxBytes <= ProjectCeiling, "project_max_bytes");
        Check(TotalMaxBytes >= ProjectMaxBytes && TotalMaxBytes <= TotalCeiling, "total_max_bytes");
        Check(AllowedTypes.Count > 0 && AllowedTypes.All(Enum.IsDefined), "allowed_types");
        Check(SourceDeny.All(Path.IsPathFullyQualified), "source_deny");
        return offending;
    }
}
