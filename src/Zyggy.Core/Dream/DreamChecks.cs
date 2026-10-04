using System.Globalization;
using System.Text.RegularExpressions;

using Zyggy.Core.Memory;

namespace Zyggy.Core.Dream;

/// <summary>
/// The safety net that replaces a daily human review (spec 28 AC-15, AC-17; plan Appendix B). <see cref="CheckBatch"/> evaluates the
/// rules in table order and returns the first one a proposal breaks; <see cref="CheckRun"/> is the run-level removal breaker.
/// </summary>
internal static partial class DreamChecks
{
    private static readonly string[] IdentityFiles = ["profile.md", "preferences.md"];

    public static DreamCheck? CheckBatch(DreamBatch batch, DreamProposal proposal, WorkingSet before, WorkingSet after, ApplyResult applied,
        DreamRunContext context)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(applied);
        ArgumentNullException.ThrowIfNull(context);
        var paths = before.Snapshot.Paths;
        var options = context.Options;
        var creates = proposal.Creates.Select(c => (Create: c, Path: Classify(paths, c.Path))).ToList();
        var edits = proposal.Edits.Select(e => (Edit: e, Path: Classify(paths, e.Path))).ToList();

        // path_refused
        if (creates.Any(c => c.Path.Kind != PathKind.SideFile) || edits.Any(e => e.Path.Kind == PathKind.Refused))
        {
            return DreamCheck.PathRefused;
        }

        var sideFiles = creates.Select(c => c.Path).Concat(edits.Select(e => e.Path)).Where(p => p.Kind == PathKind.SideFile).ToList();

        // slug_invalid
        if (sideFiles.Any(p => !Slug.TryParse(p.Slug, out _)))
        {
            return DreamCheck.SlugInvalid;
        }

        // slug_duplicate
        var usedSlugs = before.Paths.Select(SlugOf).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var newSlugs = new HashSet<string>(StringComparer.Ordinal);
        if (creates.Any(c => usedSlugs.Contains(c.Path.Slug!) || !newSlugs.Add(c.Path.Slug!)))
        {
            return DreamCheck.SlugDuplicate;
        }

        // category_invalid
        var existing = ExistingCategories(before);
        var proposedNew = proposal.NewCategories.Select(n => (n.Side, n.Name)).ToList();
        if (sideFiles.Any(p => !CategoryName.TryParse(p.Category, out _))
            || proposal.NewCategories.Any(n => !MemorySideWire.TryFromWire(n.Side, out _) || !CategoryName.TryParse(n.Name, out _))
            || creates.Any(c => !existing.Contains((c.Path.Side!, c.Path.Category!)) && !proposedNew.Contains((c.Path.Side!, c.Path.Category!)))
            || proposedNew.Any(n => !creates.Any(c => c.Path.Side == n.Side && c.Path.Category == n.Name)))
        {
            return DreamCheck.CategoryInvalid;
        }

        // category_cap
        var reallyNew = proposedNew.Where(n => !existing.Contains(n)).Distinct().ToList();
        if (reallyNew.Count > 0
            && (context.NewCategoriesSoFar + reallyNew.Count > options.MaxNewCategoriesPerRun
                || reallyNew.Select(n => n.Side).Distinct().Any(side =>
                    existing.Count(e => e.Side == side) + reallyNew.Count(n => n.Side == side) > options.MaxCategoriesPerSide)))
        {
            return DreamCheck.CategoryCap;
        }

        var added = creates.SelectMany(c => c.Create.Lines.Select(l => (Path: c.Path, Line: l, ReplaceOf: (string?)null)))
            .Concat(edits.SelectMany(e => e.Edit.Append.Select(l => (e.Path, Line: l, ReplaceOf: (string?)null))))
            .Concat(edits.SelectMany(e => e.Edit.Replace.Select(r => (e.Path, Line: r.New, ReplaceOf: (string?)r.Old))))
            .ToList();
        var descriptions = proposal.Creates.Select(c => c.Description)
            .Concat(proposal.Edits.Select(e => e.Description).OfType<string>())
            .Concat(proposal.NewCategories.Select(n => n.Description))
            .ToList();

        // format_invalid
        if (added.Any(a => a.Line.Length > MemoryLine.MaxLength || TagOf(a.Line) is not { } tag
                || (IsKnownTag(tag) && (MemoryLine.Parse(a.Line) is not { Tag: not MemoryTag.Other, Date: not null })))
            || descriptions.Any(d => d.Trim().Length == 0 || d.Length >= 150)
            || proposal.Creates.Any(c => c.Name.Trim().Length == 0))
        {
            return DreamCheck.FormatInvalid;
        }

        // foreign_tag
        if (added.Any(a => !IsKnownTag(TagOf(a.Line)!)))
        {
            return DreamCheck.ForeignTag;
        }

        var parsed = added.Select(a => (a.Path, a.Line, a.ReplaceOf, Parsed: MemoryLine.Parse(a.Line))).ToList();

        // provenance_missing
        if (parsed.Any(a => a.Parsed.Tag == MemoryTag.Observed && a.Parsed.Provenance.Count == 0))
        {
            return DreamCheck.ProvenanceMissing;
        }

        // tag_upgrade
        var statedDates = batch.Lines.Where(l => l.Class == DreamLineClass.StatedInbox)
            .Select(l => MemoryLine.Parse(l.Text).Date)
            .OfType<DateOnly>()
            .ToHashSet();
        if (parsed.Any(a => a.Parsed.Tag == MemoryTag.Stated && !statedDates.Contains(a.Parsed.Date!.Value)
                && !(a.ReplaceOf is not null && MemoryLine.Parse(a.ReplaceOf).Tag == MemoryTag.Stated)))
        {
            return DreamCheck.TagUpgrade;
        }

        // identity_observed
        if (parsed.Any(a => a.Path.Kind == PathKind.Identity && a.Parsed.Tag == MemoryTag.Observed))
        {
            return DreamCheck.IdentityObserved;
        }

        // identity_shrink
        foreach (var identity in IdentityFiles)
        {
            var was = FactLines(before.Text(identity)).Count;
            var now = FactLines(after.Text(identity)).Count;
            if (was > 0 && (double)(was - now) / was > options.IdentityMaxShrinkRatio)
            {
                return DreamCheck.IdentityShrink;
            }
        }

        var scanned = added.Select(a => a.Line)
            .Concat(descriptions)
            .Concat(proposal.Creates.SelectMany(c => c.Aliases.Append(c.Name)))
            .Concat(proposal.Edits.SelectMany(e => e.Aliases ?? []))
            .ToList();

        // secret_pattern
        if (scanned.Any(s => context.Secrets.TryMatch(s, out _)))
        {
            return DreamCheck.SecretPattern;
        }

        // contact_detail
        if (scanned.Any(ContactDetailPatterns.Contains))
        {
            return DreamCheck.ContactDetail;
        }

        // edit_mismatch
        if (applied.EditMismatch)
        {
            return DreamCheck.EditMismatch;
        }

        // removal_limit
        var removed = proposal.Edits.Sum(e => e.Remove.Count);
        var touched = edits.Select(e => e.Path.Relative!).Distinct(StringComparer.Ordinal).Sum(p => FactLines(before.Text(p)).Count);
        if (removed > options.BatchMaxRemovedLines || (touched > 0 && (double)removed / touched > options.BatchMaxRemovedRatio))
        {
            return DreamCheck.RemovalLimit;
        }

        // coverage
        var ids = batch.Lines.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        var counts = proposal.Dispositions.GroupBy(d => d.Line, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        if (counts.Keys.Any(k => !ids.Contains(k)) || ids.Any(id => !counts.TryGetValue(id, out var n) || n != 1))
        {
            return DreamCheck.Coverage;
        }

        // stated_dropped
        if (proposal.Dispositions.Any(d => d.Outcome == "dropped" && batch.Find(d.Line)?.Class == DreamLineClass.StatedInbox))
        {
            return DreamCheck.StatedDropped;
        }

        // fact_not_found
        foreach (var disposition in proposal.Dispositions.Where(d => d.Outcome is "filed" or "merged" or "duplicate"))
        {
            if (disposition.Target is null || paths.TryResolve(disposition.Target) is not { Succeeded: true } target
                || after.Text(target.RelativePath!) is not { } text || !HoldsProvenance(batch.Find(disposition.Line)!, text))
            {
                return DreamCheck.FactNotFound;
            }
        }

        // concurrent_edit
        var targets = creates.Select(c => c.Path.Relative!)
            .Concat(edits.Select(e => e.Path.Relative!))
            .Concat(proposal.Dispositions.Select(d => d.Target).OfType<string>()
                .Select(t => paths.TryResolve(t)).Where(r => r.Succeeded).Select(r => r.RelativePath!))
            .Distinct(StringComparer.Ordinal);
        if (targets.Any(t => context.CarriedPaths.Contains(t) || ChangedOnDisk(before.Snapshot, t)))
        {
            return DreamCheck.ConcurrentEdit;
        }

        return null;
    }

    /// <summary>AC-17: the run's accepted changes may remove at most <see cref="DreamOptions.RunMaxRemovedRatio"/> of the durable lines.</summary>
    public static DreamCheck? CheckRun(MemorySnapshot snapshot, WorkingSet set, DreamOptions options)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(options);
        var durable = snapshot.Files.Values.Where(f => IsDurable(snapshot.Paths, f.RelativePath)).ToList();
        var total = durable.Sum(f => FactLines(f.Text).Count);
        if (total == 0)
        {
            return null;
        }

        var removed = 0;
        foreach (var file in durable)
        {
            var now = FactLines(set.Text(file.RelativePath)).GroupBy(l => l, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count());
            foreach (var group in FactLines(file.Text).GroupBy(l => l, StringComparer.Ordinal))
            {
                removed += Math.Max(0, group.Count() - (now.TryGetValue(group.Key, out var n) ? n : 0));
            }
        }

        return (double)removed / total > options.RunMaxRemovedRatio ? DreamCheck.RunRemovalLimit : null;
    }

    internal static List<string> FactLines(string? text) =>
        text is null ? [] : MemorySnapshot.BodyLines(text).Select(l => l.Text).ToList();

    private static bool IsDurable(MemoryPaths paths, string relative) =>
        paths.TryResolve(relative) is { Succeeded: true, Area: MemoryArea.Durable or MemoryArea.Identity or MemoryArea.Legacy };

    private static bool ChangedOnDisk(MemorySnapshot snapshot, string relative)
    {
        var full = Path.Join(snapshot.Paths.PrincipalDirectory, relative);
        if (!snapshot.Files.TryGetValue(relative, out var file))
        {
            return File.Exists(full);
        }

        return !File.Exists(full) || MemorySnapshot.Hash(File.ReadAllBytes(full)) != file.Sha256;
    }

    // Appendix B "Provenance token of a source line".
    private static bool HoldsProvenance(DreamBatchLine source, string targetText)
    {
        var lines = FactLines(targetText).Select(MemoryLine.Parse).ToList();
        var parsed = MemoryLine.Parse(source.Text);
        if (source.Class == DreamLineClass.Daily && source.FileDate is { } day)
        {
            return lines.Any(l => l.Provenance.Contains($"daily {Iso(day)}"));
        }

        if (parsed.Tag == MemoryTag.Stated && parsed.Date is { } stated)
        {
            return lines.Any(l => (l.Tag == MemoryTag.Stated && l.Date == stated) || l.Provenance.Contains($"remember {Iso(stated)}"));
        }

        if (parsed.Tag == MemoryTag.Observed && parsed.Provenance.Count > 0)
        {
            return parsed.Provenance.All(token => lines.Any(l => l.Provenance.Contains(token)));
        }

        return true;
    }

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string? TagOf(string line) => Tag().Match(line) is { Success: true } m ? m.Groups[1].Value : null;

    private static bool IsKnownTag(string tag) => tag is "stated" or "observed";

    private static HashSet<(string Side, string Category)> ExistingCategories(WorkingSet set) =>
        set.Paths.Select(p => p.Split('/'))
            .Where(s => s.Length == 3 && MemorySideWire.TryFromWire(s[0], out _) && CategoryName.TryParse(s[1], out _))
            .Select(s => (s[0], s[1]))
            .ToHashSet();

    // The slug of any memory file in the tree: <side>/<category>/<slug>.md or a legacy <areas|people|topics>/<slug>.md.
    private static string? SlugOf(string relative)
    {
        var s = relative.Split('/');
        var name = s[^1];
        if (!name.EndsWith(".md", StringComparison.Ordinal) || name.StartsWith('_'))
        {
            return null;
        }

        var sided = s.Length == 3 && MemorySideWire.TryFromWire(s[0], out _);
        var legacy = s.Length == 2 && s[0] is "areas" or "people" or "topics";
        return sided || legacy ? name[..^3] : null;
    }

    private static ProposedPath Classify(MemoryPaths paths, string raw)
    {
        var resolution = paths.TryResolve(raw);
        if (resolution.Refusal is MemoryPathRefusal.Absolute or MemoryPathRefusal.Traversal or MemoryPathRefusal.OutsidePrincipal
            or MemoryPathRefusal.SymlinkEscape)
        {
            return new ProposedPath(PathKind.Refused, null, null, null, null);
        }

        var segments = raw.Split('/', '\\');
        if (segments.Length == 1 && IdentityFiles.Contains(segments[0]))
        {
            return new ProposedPath(PathKind.Identity, segments[0], null, null, null);
        }

        if (segments.Length == 3 && MemorySideWire.TryFromWire(segments[0], out _) && segments[2].EndsWith(".md", StringComparison.Ordinal)
            && !segments[2].StartsWith('_'))
        {
            return new ProposedPath(PathKind.SideFile, string.Join('/', segments), segments[0], segments[1], segments[2][..^3]);
        }

        return new ProposedPath(PathKind.Refused, null, null, null, null);
    }

    [GeneratedRegex(@"^- \[([^\]]+)\] \S", RegexOptions.CultureInvariant)]
    private static partial Regex Tag();

    private enum PathKind
    {
        Refused,
        Identity,
        SideFile,
    }

    private sealed record ProposedPath(PathKind Kind, string? Relative, string? Side, string? Category, string? Slug);
}
