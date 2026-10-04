using Zyggy.Core.Memory;

namespace Zyggy.Core.Dream;

/// <summary>What applying a proposal did.</summary>
internal sealed record ApplyResult(bool EditMismatch, int FilesCreated, int FilesEdited, int CategoriesCreated);

/// <summary>
/// Applies a filing proposal to a working set, in memory only (Appendix B "Apply"): new categories, then creates, then edits
/// (remove, replace, append, description, aliases); <c>updated</c> becomes the run's local date wherever something changed.
/// Paths the layout refuses are skipped here and refused by the checks.
/// </summary>
internal static class ProposalApplier
{
    public static ApplyResult Apply(DreamProposal proposal, MemorySnapshot snapshot, WorkingSet set, DateOnly runDate)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(set);

        var categories = 0;
        foreach (var category in proposal.NewCategories)
        {
            if (Resolve(snapshot, $"{category.Side}/{category.Name}/_index.md") is { } path && !set.Exists(path))
            {
                set.Write(path, MemoryFileWriter.Render(new MemoryFile(category.Name, category.Description, [], runDate, Empty, true, [])));
                categories++;
            }
        }

        var created = 0;
        foreach (var create in proposal.Creates)
        {
            if (Resolve(snapshot, create.Path) is { } path)
            {
                set.Write(path, MemoryFileWriter.Render(new MemoryFile(create.Name, create.Description, create.Aliases, runDate, Empty, true, create.Lines)));
                created++;
            }
        }

        var edited = 0;
        var mismatch = false;
        foreach (var edit in proposal.Edits)
        {
            if (Resolve(snapshot, edit.Path) is not { } path || set.Text(path) is not { } text)
            {
                mismatch |= Resolve(snapshot, edit.Path) is not null;
                continue;
            }

            MemoryFile file;
            try
            {
                file = MemoryFileReader.Parse(text);
            }
            catch (FormatException)
            {
                mismatch = true;
                continue;
            }

            var lines = file.BodyLines.ToList();
            foreach (var remove in edit.Remove)
            {
                mismatch |= !lines.Remove(remove.Old);
            }

            foreach (var replace in edit.Replace)
            {
                var at = lines.IndexOf(replace.Old);
                if (at < 0)
                {
                    mismatch = true;
                }
                else
                {
                    lines[at] = replace.New;
                }
            }

            lines.AddRange(edit.Append);
            var updated = file with
            {
                BodyLines = lines,
                Description = edit.Description ?? file.Description,
                Aliases = edit.Aliases ?? file.Aliases,
            };
            if (updated.BodyLines.SequenceEqual(file.BodyLines) && updated.Description == file.Description && updated.Aliases.SequenceEqual(file.Aliases))
            {
                continue;
            }

            updated = updated with
            {
                Updated = runDate,
                HasFrontMatter = true,
                Name = file.Name ?? Path.GetFileNameWithoutExtension(path),
            };
            set.Write(path, MemoryFileWriter.Render(updated));
            edited++;
        }

        return new ApplyResult(mismatch, created, edited, categories);
    }

    private static readonly Dictionary<string, string> Empty = new(StringComparer.Ordinal);

    private static string? Resolve(MemorySnapshot snapshot, string path) =>
        snapshot.Paths.TryResolve(path) is { Succeeded: true } resolution ? resolution.RelativePath : null;
}
