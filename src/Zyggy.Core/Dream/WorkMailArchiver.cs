using System.Security.Cryptography;
using System.Text;

using Zyggy.Core.Memory;

namespace Zyggy.Core.Dream;

/// <summary>One business-archive write the proposal asked for, resolved: the entry and the item and sidecar paths.</summary>
/// <param name="Entry">The proposal's entry.</param>
/// <param name="ItemPath">The item, relative to the principal directory (<c>archive/&lt;project&gt;/&lt;slug&gt;.txt</c>).</param>
/// <param name="SidecarPath">The sidecar (<c>archive/&lt;project&gt;/&lt;slug&gt;.md</c>).</param>
internal sealed record WorkMailArchiveWrite(ArchiveWorkMail Entry, string ItemPath, string SidecarPath);

/// <summary>
/// The dream's one exception to "the dream never writes <c>archive/</c>" (spec 58 D-Q15, template rule change 6): a work mail line from
/// the work laptop may be kept whole in the business archive. The program, not the model, writes the item — the line's fact as
/// <c>text/plain</c> — and its sidecar (spec 37 format, <c>source_name</c> the line's <c>work-mail</c> token). Which entries are allowed
/// (work mail only, a business project only, indexed on the business side, never over an existing item) is decided by
/// <see cref="DreamChecks"/>; this class only writes what resolves.
/// </summary>
internal static class WorkMailArchiver
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Resolves an entry's paths; <see langword="null"/> when its project or slug is not a valid slug.</summary>
    /// <param name="paths">The memory paths.</param>
    /// <param name="entry">The entry.</param>
    /// <returns>The write, or <see langword="null"/>.</returns>
    public static WorkMailArchiveWrite? Resolve(MemoryPaths paths, ArchiveWorkMail entry)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(entry);
        if (!Slug.TryParse(entry.Project, out var project) || !Slug.TryParse(entry.Slug, out var slug))
        {
            return null;
        }

        return new WorkMailArchiveWrite(
            entry,
            paths.Relative(paths.ArchiveItem(project, slug, ArchiveMediaType.TextPlain)),
            paths.Relative(paths.ArchiveSidecar(project, slug)));
    }

    /// <summary>Whether the item or its sidecar already exists (in the set, as an archived item of the snapshot, or on disk).</summary>
    /// <param name="set">The working set.</param>
    /// <param name="write">The write.</param>
    /// <returns>Whether the slug is taken.</returns>
    public static bool Taken(WorkingSet set, WorkMailArchiveWrite write)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(write);
        var root = set.Snapshot.Paths.PrincipalDirectory;
        return set.Exists(write.ItemPath) || set.Exists(write.SidecarPath) || set.Snapshot.ArchiveItems.ContainsKey(write.ItemPath)
            || File.Exists(Path.Join(root, write.ItemPath)) || File.Exists(Path.Join(root, write.SidecarPath));
    }

    /// <summary>The item's text: the work mail line's fact, one line.</summary>
    /// <param name="line">The work mail inbox line.</param>
    /// <returns>The text.</returns>
    public static string ItemText(DreamBatchLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return (MemoryLine.Parse(line.Text).Fact ?? string.Empty) + "\n";
    }

    /// <summary>Writes every entry that names a work mail line of the batch and resolves, into the working set (in memory only).</summary>
    /// <param name="proposal">The proposal.</param>
    /// <param name="batch">The batch.</param>
    /// <param name="set">The working set.</param>
    /// <param name="runDate">The run's local date.</param>
    public static void Apply(DreamProposal proposal, DreamBatch batch, WorkingSet set, DateOnly runDate)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(set);
        foreach (var entry in proposal.Archives ?? [])
        {
            if (batch.Find(entry.Line) is not { } line || !DreamChecks.IsWorkMailLine(line)
                || Resolve(set.Snapshot.Paths, entry) is not { } write || Taken(set, write))
            {
                continue;
            }

            var text = ItemText(line);
            var bytes = Utf8NoBom.GetBytes(text);
            var token = MemoryLine.Parse(line.Text).Provenance.First(DreamChecks.IsWorkMailToken);
            var sidecar = new ArchiveSidecar(entry.Name, entry.Description, Slug.Parse(entry.Project), ArchiveMediaType.TextPlain, bytes.LongLength,
                Convert.ToHexStringLower(SHA256.HashData(bytes)), runDate, token, runDate);
            set.Write(write.ItemPath, text);
            set.Write(write.SidecarPath, MemoryFileWriter.Render(sidecar.ToMemoryFile()));
        }
    }
}
