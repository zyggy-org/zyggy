using System.Security.Cryptography;

namespace Zyggy.Core.Memory;

/// <summary>
/// An item the run will move: its project and slug, the full paths of item and sidecar, the parsed sidecar, the item's size on disk, and
/// whether it is an interrupted offload to finish (sidecar already written, item still in git — spec 38 AC-17).
/// </summary>
internal sealed record OffloadCandidate(Slug Project, Slug Slug, string ItemPath, string SidecarPath, ArchiveSidecar Sidecar, long Size, bool Resume);

/// <summary>The candidates of one run, and the eligible items left for a later run by the per-run caps.</summary>
internal sealed record OffloadPlan(IReadOnlyList<OffloadCandidate> Candidates, IReadOnlyList<OffloadCandidate> Deferred);

/// <summary>
/// Which archive items move (spec 38 AC-1, AC-2): <c>indexed</c> items whose bytes are present, whose sidecar parses without an offload
/// part, not parked, not excluded and at least <c>min_bytes</c> — interrupted offloads first — in ordinal path order, within the per-run
/// caps (the first candidate always moves; plan assumption A11). Selection reads no item bytes; <see cref="ReadAndCheck"/> reads them once.
/// </summary>
internal static class OffloadSelection
{
    public static OffloadPlan Select(IReadOnlyList<ArchiveListRow> rows, MemoryPaths paths, OffloadOptions options, OffloadState state)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(state);

        var excluded = options.Exclude.ToHashSet(StringComparer.Ordinal);
        var eligible = new List<OffloadCandidate>();
        foreach (var row in rows)
        {
            if (row.State == ArchiveIndexState.Offloaded || row.ItemPath is null || row.SidecarPath is null)
            {
                continue;
            }

            var key = OffloadState.Key(row.Project, row.Slug);
            if (excluded.Contains(key) || state.Items.TryGetValue(key, out var failures) && failures.Parked)
            {
                continue;
            }

            var itemPath = paths.ArchiveItem(row.Project, row.Slug, row.Type ?? ArchiveMediaType.TextPlain);
            var sidecarPath = paths.ArchiveSidecar(row.Project, row.Slug);
            var item = new FileInfo(itemPath);
            if (!item.Exists || ArchiveSidecar.TryParse(MemoryFileReader.Read(sidecarPath)) is not { } sidecar)
            {
                continue;
            }

            var resume = sidecar.Offload is not null;
            if (resume || (row.State == ArchiveIndexState.Indexed && item.Length >= options.MinBytes))
            {
                eligible.Add(new OffloadCandidate(row.Project, row.Slug, itemPath, sidecarPath, sidecar, item.Length, resume));
            }
        }

        var ordered = eligible.OrderByDescending(c => c.Resume).ThenBy(c => paths.Relative(c.ItemPath), StringComparer.Ordinal).ToList();
        var taken = new List<OffloadCandidate>();
        long bytes = 0;
        foreach (var candidate in ordered)
        {
            if (taken.Count > 0 && (taken.Count >= options.MaxItemsPerRun || bytes + candidate.Size > options.MaxBytesPerRun))
            {
                break;
            }

            taken.Add(candidate);
            bytes += candidate.Size;
        }

        return new OffloadPlan(taken, ordered.Skip(taken.Count).ToList());
    }

    /// <summary>The item's bytes, read once, when their SHA-256 equals the sidecar's; else <see cref="OffloadReason.ShaMismatch"/> and no bytes.</summary>
    public static (byte[]? Bytes, OffloadReason? Reason) ReadAndCheck(OffloadCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var bytes = File.ReadAllBytes(candidate.ItemPath);
        return string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), candidate.Sidecar.Sha256, StringComparison.Ordinal)
            ? (bytes, null)
            : (null, OffloadReason.ShaMismatch);
    }
}
