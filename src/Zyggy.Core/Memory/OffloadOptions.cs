namespace Zyggy.Core.Memory;

/// <summary>
/// The <c>offload</c> block of <c>instance/archive.json</c> (spec 38 Configuration): off by default; a value outside its range is a
/// configuration error naming <c>offload.&lt;key&gt;</c>, never clamped.
/// </summary>
public sealed record OffloadOptions
{
    /// <summary>The most items one run may move.</summary>
    public const int MaxItemsCeiling = 200;

    /// <summary>The smallest per-run byte cap (1 MiB).</summary>
    public const long MaxBytesMinimum = 1_048_576;

    /// <summary>The largest per-run byte cap (1 GiB).</summary>
    public const long MaxBytesCeiling = 1_073_741_824;

    /// <summary>The most consecutive transient failures before an item is parked.</summary>
    public const int MaxFailuresCeiling = 10;

    /// <summary>Gets a value indicating whether the offload runs at all (the kill switch; false in the template).</summary>
    public bool Enabled { get; init; }

    /// <summary>Gets the owner-created OneDrive folder items move under: one to three plain folder names joined by <c>/</c>.</summary>
    public string RootFolder { get; init; } = "Zyggy/archive";

    /// <summary>Gets the smallest item that moves, in bytes (0: every indexed item).</summary>
    public long MinBytes { get; init; }

    /// <summary>Gets the most items one run moves.</summary>
    public int MaxItemsPerRun { get; init; } = 20;

    /// <summary>Gets the most bytes one run moves (200 MiB); the first candidate of a run always moves.</summary>
    public long MaxBytesPerRun { get; init; } = 209_715_200;

    /// <summary>Gets the consecutive transient failures after which an item is parked.</summary>
    public int MaxFailures { get; init; } = 3;

    /// <summary>Gets the items that never move, as <c>&lt;project&gt;/&lt;slug&gt;</c>.</summary>
    public IReadOnlyList<string> Exclude { get; init; } = [];

    /// <summary>Returns the <c>offload.*</c> keys whose value is outside its range.</summary>
    /// <returns>The offending keys in table order; empty when valid.</returns>
    public IReadOnlyList<string> Validate()
    {
        var offending = new List<string>();
        void Check(bool ok, string key)
        {
            if (!ok)
            {
                offending.Add("offload." + key);
            }
        }

        Check(IsRootFolder(RootFolder), "root_folder");
        Check(MinBytes >= 0 && MinBytes <= ArchiveOptions.ItemCeiling, "min_bytes");
        Check(MaxItemsPerRun >= 1 && MaxItemsPerRun <= MaxItemsCeiling, "max_items_per_run");
        Check(MaxBytesPerRun >= MaxBytesMinimum && MaxBytesPerRun <= MaxBytesCeiling, "max_bytes_per_run");
        Check(MaxFailures >= 1 && MaxFailures <= MaxFailuresCeiling, "max_failures");
        Check(Exclude.All(IsItemKey), "exclude");
        return offending;
    }

    private static bool IsRootFolder(string? root)
    {
        if (string.IsNullOrEmpty(root))
        {
            return false;
        }

        var segments = root.Split('/');
        return segments.Length <= 3 && segments.All(s =>
            s.Length > 0 && s is not ("." or "..") && s.Trim() == s && !s.Contains('\\', StringComparison.Ordinal) && !s.Any(char.IsControl));
    }

    private static bool IsItemKey(string? key) =>
        key?.Split('/') is [var project, var slug] && Slug.TryParse(project, out _) && Slug.TryParse(slug, out _) && !key.Contains('\n', StringComparison.Ordinal);
}
