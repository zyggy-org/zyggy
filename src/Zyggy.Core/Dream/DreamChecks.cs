using System.Globalization;
using System.Text.RegularExpressions;

using Zyggy.Core.Memory;

namespace Zyggy.Core.Dream;

/// <summary>A refused rule and a fact-free detail for the run record and the journal.</summary>
internal sealed record DreamCheckResult(DreamCheck Check, string Detail);

/// <summary>
/// The safety net that replaces a daily human review (spec 28 AC-15, AC-17; plan Appendix B).
/// </summary>
internal static partial class DreamChecks
{
    private static readonly string[] IdentityFiles = ["profile.md", "preferences.md"];

    /// <summary>
    /// Returns the first rule the proposal breaks with a fact-free detail (paths, line ids, counts, pattern names — never a fact
    /// text), or <see langword="null"/> when it passes.
    /// </summary>
    public static DreamCheckResult? CheckBatch(DreamBatch batch, DreamProposal proposal, WorkingSet before, WorkingSet after, ApplyResult applied,
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
        var refused = creates.Where(c => c.Path.Kind != PathKind.SideFile).Select(c => "create " + Safe(c.Create.Path))
            .Concat(edits.Where(e => e.Path.Kind == PathKind.Refused).Select(e => "edit " + Safe(e.Edit.Path)))
            .FirstOrDefault();
        if (refused is not null)
        {
            return Fail(DreamCheck.PathRefused, refused);
        }

        var sideFiles = creates.Select(c => c.Path).Concat(edits.Select(e => e.Path)).Where(p => p.Kind == PathKind.SideFile).ToList();

        // slug_invalid
        if (sideFiles.FirstOrDefault(p => !Slug.TryParse(p.Slug, out _)) is { } badSlug)
        {
            return Fail(DreamCheck.SlugInvalid, "file " + Safe(badSlug.Relative!));
        }

        // slug_duplicate
        var usedSlugs = before.Paths.Select(SlugOf).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var newSlugs = new HashSet<string>(StringComparer.Ordinal);
        if (creates.FirstOrDefault(c => usedSlugs.Contains(c.Path.Slug!) || !newSlugs.Add(c.Path.Slug!)) is { Create: not null } duplicate)
        {
            return Fail(DreamCheck.SlugDuplicate, "create " + Safe(duplicate.Path.Relative!));
        }

        // category_invalid
        var existing = ExistingCategories(before);
        var proposedNew = proposal.NewCategories.Select(n => (n.Side, n.Name)).ToList();
        var categoryProblem =
            sideFiles.Where(p => !CategoryName.TryParse(p.Category, out _)).Select(p => "bad category in " + Safe(p.Relative!))
            .Concat(proposal.NewCategories.Where(n => !MemorySideWire.TryFromWire(n.Side, out _) || !CategoryName.TryParse(n.Name, out _))
                .Select(n => $"bad new category {Safe(n.Side)}/{Safe(n.Name)}"))
            .Concat(creates.Where(c => !existing.Contains((c.Path.Side!, c.Path.Category!)) && !proposedNew.Contains((c.Path.Side!, c.Path.Category!)))
                .Select(c => "unknown category for " + Safe(c.Path.Relative!)))
            .Concat(proposedNew.Where(n => !creates.Any(c => c.Path.Side == n.Side && c.Path.Category == n.Name))
                .Select(n => $"new category {Safe(n.Side)}/{Safe(n.Name)} without a file"))
            .FirstOrDefault();
        if (categoryProblem is not null)
        {
            return Fail(DreamCheck.CategoryInvalid, categoryProblem);
        }

        // category_cap
        var reallyNew = proposedNew.Where(n => !existing.Contains(n)).Distinct().ToList();
        if (reallyNew.Count > 0
            && (context.NewCategoriesSoFar + reallyNew.Count > options.MaxNewCategoriesPerRun
                || reallyNew.Select(n => n.Side).Distinct().Any(side =>
                    existing.Count(e => e.Side == side) + reallyNew.Count(n => n.Side == side) > options.MaxCategoriesPerSide)))
        {
            return Fail(DreamCheck.CategoryCap, $"new {reallyNew.Count}, earlier this run {context.NewCategoriesSoFar}");
        }

        var added = creates.SelectMany(c => c.Create.Lines.Select((l, i) => (Path: c.Path, Line: l, ReplaceOf: (string?)null, Where: $"create {Safe(c.Path.Relative!)} line {i + 1}")))
            .Concat(edits.SelectMany(e => e.Edit.Append.Select((l, i) => (e.Path, Line: l, ReplaceOf: (string?)null, Where: $"append {Safe(e.Path.Relative!)} #{i + 1}"))))
            .Concat(edits.SelectMany(e => e.Edit.Replace.Select((r, i) => (e.Path, Line: r.New, ReplaceOf: (string?)r.Old, Where: $"replace {Safe(e.Path.Relative!)} #{i + 1}"))))
            .ToList();
        var descriptions = proposal.Creates.Select(c => (Text: c.Description, Where: "description of " + Safe(c.Path)))
            .Concat(proposal.Edits.Where(e => e.Description is not null).Select(e => (Text: e.Description!, Where: "description of " + Safe(e.Path))))
            .Concat(proposal.NewCategories.Select(n => (Text: n.Description, Where: $"description of {Safe(n.Side)}/{Safe(n.Name)}")))
            .ToList();

        // format_invalid
        var badFormat = added.Where(a => a.Line.Length > MemoryLine.MaxLength).Select(a => $"{a.Where}: {a.Line.Length} chars")
            .Concat(added.Where(a => a.Line.Length <= MemoryLine.MaxLength && (TagOf(a.Line) is not { } tag
                    || (IsKnownTag(tag) && MemoryLine.Parse(a.Line) is not { Tag: not MemoryTag.Other, Date: not null })))
                .Select(a => a.Where + ": not a memory line"))
            .Concat(descriptions.Where(d => d.Text.Trim().Length == 0 || d.Text.Length >= 150).Select(d => $"{d.Where}: {d.Text.Length} chars"))
            .Concat(proposal.Creates.Where(c => c.Name.Trim().Length == 0).Select(c => "empty name for " + Safe(c.Path)))
            .FirstOrDefault();
        if (badFormat is not null)
        {
            return Fail(DreamCheck.FormatInvalid, badFormat);
        }

        // foreign_tag
        if (added.FirstOrDefault(a => !IsKnownTag(TagOf(a.Line)!)) is { Where: not null } foreign)
        {
            return Fail(DreamCheck.ForeignTag, $"{foreign.Where}: tag {Safe(TagOf(foreign.Line)!)}");
        }

        var parsed = added.Select(a => (a.Path, a.Line, a.ReplaceOf, a.Where, Parsed: MemoryLine.Parse(a.Line))).ToList();

        // provenance_missing
        if (parsed.FirstOrDefault(a => a.Parsed.Tag == MemoryTag.Observed && a.Parsed.Provenance.Count == 0) is { Where: not null } unsourced)
        {
            return Fail(DreamCheck.ProvenanceMissing, unsourced.Where);
        }

        // tag_upgrade
        var statedDates = batch.Lines.Where(l => l.Class == DreamLineClass.StatedInbox)
            .Select(l => MemoryLine.Parse(l.Text).Date)
            .OfType<DateOnly>()
            .ToHashSet();
        if (parsed.FirstOrDefault(a => a.Parsed.Tag == MemoryTag.Stated && !statedDates.Contains(a.Parsed.Date!.Value)
                && !(a.ReplaceOf is not null && MemoryLine.Parse(a.ReplaceOf).Tag == MemoryTag.Stated)) is { Where: not null } upgrade)
        {
            return Fail(DreamCheck.TagUpgrade, upgrade.Where);
        }

        // identity_observed
        if (parsed.FirstOrDefault(a => a.Path.Kind == PathKind.Identity && a.Parsed.Tag == MemoryTag.Observed) is { Where: not null } identityObserved)
        {
            return Fail(DreamCheck.IdentityObserved, identityObserved.Where);
        }

        // identity_shrink
        foreach (var identity in IdentityFiles)
        {
            var was = FactLines(before.Text(identity)).Count;
            var now = FactLines(after.Text(identity)).Count;
            if (was > 0 && (double)(was - now) / was > options.IdentityMaxShrinkRatio)
            {
                return Fail(DreamCheck.IdentityShrink, $"{identity}: {was} -> {now} lines");
            }
        }

        var scanned = added.Select(a => (Text: a.Line, a.Where))
            .Concat(descriptions)
            .Concat(proposal.Creates.SelectMany(c => c.Aliases.Append(c.Name).Select(x => (Text: x, Where: "name or alias of " + Safe(c.Path)))))
            .Concat(proposal.Edits.SelectMany(e => (e.Aliases ?? []).Select(x => (Text: x, Where: "alias of " + Safe(e.Path)))))
            .ToList();

        // secret_pattern
        foreach (var (text, where) in scanned)
        {
            if (context.Secrets.TryMatch(text, out var pattern))
            {
                return Fail(DreamCheck.SecretPattern, $"{where}: pattern {pattern}");
            }
        }

        // contact_detail
        if (scanned.FirstOrDefault(s => ContactDetailPatterns.Contains(s.Text)) is { Where: not null } contact)
        {
            return Fail(DreamCheck.ContactDetail, contact.Where);
        }

        // edit_mismatch
        if (applied.EditMismatch)
        {
            return Fail(DreamCheck.EditMismatch, MismatchDetail(applied.Mismatch, before));
        }

        // removal_limit
        var removed = proposal.Edits.Sum(e => e.Remove.Count);
        var touched = edits.Select(e => e.Path.Relative!).Distinct(StringComparer.Ordinal).Sum(p => FactLines(before.Text(p)).Count);
        if (removed > options.BatchMaxRemovedLines || (touched > 0 && (double)removed / touched > options.BatchMaxRemovedRatio))
        {
            return Fail(DreamCheck.RemovalLimit, $"removed {removed} of {touched} lines in the edited files");
        }

        // coverage
        var ids = batch.Lines.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        var counts = proposal.Dispositions.GroupBy(d => d.Line, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var unknownIds = counts.Keys.Where(k => !ids.Contains(k)).Select(Safe).ToList();
        var missingIds = ids.Where(id => !counts.ContainsKey(id)).ToList();
        var doubleIds = ids.Where(id => counts.TryGetValue(id, out var n) && n > 1).ToList();
        if (unknownIds.Count > 0 || missingIds.Count > 0 || doubleIds.Count > 0)
        {
            return Fail(DreamCheck.Coverage,
                $"missing {Ids(missingIds)}; double {Ids(doubleIds)}; unknown {Ids(unknownIds)} (of {ids.Count} lines)");
        }

        // stated_dropped
        if (proposal.Dispositions.FirstOrDefault(d => d.Outcome == "dropped" && batch.Find(d.Line)?.Class == DreamLineClass.StatedInbox) is { } dropped)
        {
            return Fail(DreamCheck.StatedDropped, $"{dropped.Line} ({Safe(dropped.DropReason ?? "no reason")})");
        }

        // fact_not_found
        foreach (var disposition in proposal.Dispositions.Where(d => d.Outcome is "filed" or "merged" or "duplicate"))
        {
            if (disposition.Target is null)
            {
                return Fail(DreamCheck.FactNotFound, $"{disposition.Line} {disposition.Outcome} without a target");
            }

            if (paths.TryResolve(disposition.Target) is not { Succeeded: true } target || after.Text(target.RelativePath!) is not { } text)
            {
                return Fail(DreamCheck.FactNotFound, $"{disposition.Line} {disposition.Outcome} into missing {Safe(disposition.Target)}");
            }

            if (!HoldsProvenance(batch.Find(disposition.Line)!, text))
            {
                return Fail(DreamCheck.FactNotFound, $"{disposition.Line} {disposition.Outcome}: {Safe(target.RelativePath!)} lacks its provenance");
            }
        }

        // concurrent_edit
        var targets = creates.Select(c => c.Path.Relative!)
            .Concat(edits.Select(e => e.Path.Relative!))
            .Concat(proposal.Dispositions.Select(d => d.Target).OfType<string>()
                .Select(t => paths.TryResolve(t)).Where(r => r.Succeeded).Select(r => r.RelativePath!))
            .Distinct(StringComparer.Ordinal);
        if (targets.FirstOrDefault(t => context.CarriedPaths.Contains(t) || ChangedOnDisk(before.Snapshot, t)) is { } busy)
        {
            return Fail(DreamCheck.ConcurrentEdit, (context.CarriedPaths.Contains(busy) ? "carried " : "changed on disk ") + busy);
        }

        return null;
    }

    private static DreamCheckResult Fail(DreamCheck check, string detail) => new(check, detail);

    private static string Ids(List<string> ids) => ids.Count == 0 ? "none" : string.Join(',', ids.Take(10)) + (ids.Count > 10 ? ",…" : string.Empty);

    // A path or name from the model, shortened and reduced to path characters so no free text reaches the record.
    private static string Safe(string text)
    {
        var kept = new string(text.Where(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '-' or '_').Take(120).ToArray());
        return kept.Length == 0 ? "?" : kept;
    }

    // edit_mismatch: which file and edit, the nearest existing line by edit distance, and whether the exact text is elsewhere.
    private static string MismatchDetail(EditMismatchInfo? mismatch, WorkingSet before)
    {
        if (mismatch is null)
        {
            return "unknown";
        }

        var text = before.Text(mismatch.Path);
        if (text is null)
        {
            return $"{mismatch.Kind} in missing file {Safe(mismatch.Path)}";
        }

        var lines = TextLines(text);
        var best = (Line: 0, Distance: int.MaxValue);
        for (var i = 0; i < lines.Count; i++)
        {
            var distance = Distance(lines[i], mismatch.Old, best.Distance);
            if (distance < best.Distance)
            {
                best = (i + 1, distance);
            }
        }

        var elsewhere = before.Paths.FirstOrDefault(p => p != mismatch.Path && p.EndsWith(".md", StringComparison.Ordinal)
            && before.Text(p) is { } other && TextLines(other).Contains(mismatch.Old));
        var nearest = best.Line == 0 ? "no lines" : $"nearest file line {best.Line} at distance {best.Distance}";
        return $"{mismatch.Kind} #{mismatch.Index} in {Safe(mismatch.Path)} ({lines.Count} lines): old has {mismatch.Old.Length} chars, "
            + $"{nearest}; exact text {(elsewhere is null ? "in no other file" : "in " + elsewhere)}";
    }

    private static List<string> TextLines(string text) => Memory.TextLines.Split(text);

    // Levenshtein distance, giving up (returning the cap) once it cannot beat the cap.
    private static int Distance(string a, string b, int cap)
    {
        if (Math.Abs(a.Length - b.Length) >= cap)
        {
            return cap;
        }

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var rowMin = current[0];
            for (var j = 1; j <= b.Length; j++)
            {
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                rowMin = Math.Min(rowMin, current[j]);
            }

            if (rowMin >= cap)
            {
                return cap;
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
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
