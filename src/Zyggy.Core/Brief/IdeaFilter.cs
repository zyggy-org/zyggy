using System.Text.RegularExpressions;

using Zyggy.Core.Memory;

namespace Zyggy.Core.Brief;

/// <summary>What the filter checks a suggestion against: the day, the allowed areas, the cap, the windows, the history and the memory.</summary>
internal sealed record IdeaFilterContext(
    DateOnly Today,
    IReadOnlyCollection<string> AllowedAreas,
    int Cap,
    int RepeatDays,
    int SuppressDays,
    IReadOnlyList<IdeaRow> History,
    MemoryPaths Memory,
    SecretPatterns Secrets);

/// <summary>
/// Spec 35 AC-34: the binary's own check of every suggestion, in this order — area, basis, repeat, answered, area shown on the previous brief
/// day, text — then the cap. A dropped suggestion is counted by reason; nothing is ever added to fill the cap. Pure apart from reading the
/// basis files through <see cref="MemoryPaths.TryResolve"/> (never outside the principal directory).
/// </summary>
internal static partial class IdeaFilter
{
    // A basis line shorter than this proves nothing: almost any file contains it.
    private const int MinimumBasisCharacters = 10;

    public static (IReadOnlyList<IdeaSuggestion> Kept, IReadOnlyDictionary<string, int> Dropped) Filter(IReadOnlyList<IdeaSuggestion> suggestions, IdeaFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(suggestions);
        ArgumentNullException.ThrowIfNull(context);
        var kept = new List<IdeaSuggestion>();
        var dropped = new Dictionary<string, int>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var shown = context.History.Where(r => r.Kind == "shown").ToList();
        foreach (var s in suggestions)
        {
            var reason = !context.AllowedAreas.Contains(s.Area) ? "area"
                : !s.Basis.All(b => BasisHolds(b, context.Memory)) ? "basis"
                : !seen.Add(s.Id) || ShownRecently(s, shown, context) ? "repeat"
                : Answered(s, context) ? "answered"
                : AreaShownYesterday(s, shown, context.Today) ? "area-yesterday"
                : TextWithheld(s, context.Secrets) ? "text"
                : kept.Count >= context.Cap ? "cap"
                : null;
            if (reason is null)
            {
                kept.Add(s);
            }
            else
            {
                dropped[reason] = dropped.GetValueOrDefault(reason) + 1;
            }
        }

        return (kept, dropped);
    }

    // The file is inside the principal directory, not a mail-derived or remember inbox file (only the GitHub inventory is readable there),
    // not the dream's own state; the line occurs in it.
    private static bool BasisHolds(IdeaBasis basis, MemoryPaths memory)
    {
        var resolution = memory.TryResolve(basis.File);
        if (!resolution.Succeeded || resolution.FullPath is not { } full || resolution.RelativePath is not { } relative)
        {
            return false;
        }

        relative = relative.Replace('\\', '/');
        if ((relative.StartsWith("inbox/", StringComparison.Ordinal) && !GithubInventory().IsMatch(relative)) || relative.StartsWith(".dream/", StringComparison.Ordinal))
        {
            return false;
        }

        var needle = basis.Line.Trim();
        if (needle.Length < MinimumBasisCharacters || !File.Exists(full))
        {
            return false;
        }

        try
        {
            return File.ReadLines(full).Any(line => line.Contains(needle, StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Shown within ideas_repeat_days, unless the deadline is now earlier than when it was shown.
    private static bool ShownRecently(IdeaSuggestion s, List<IdeaRow> shown, IdeaFilterContext context)
    {
        var last = shown.Where(r => r.Id == s.Id && r.Date > context.Today.AddDays(-context.RepeatDays)).OrderBy(r => r.Date).LastOrDefault();
        if (last is null)
        {
            return false;
        }

        var earlier = s.Deadline is { } now && (last.Deadline is null || now < last.Deadline);
        return !earlier;
    }

    private static bool Answered(IdeaSuggestion s, IdeaFilterContext context) =>
        context.History.Any(r => r.Kind == "answer" && r.Id == s.Id
            && ((r.Answer == "not-interested" && r.Date > context.Today.AddDays(-context.SuppressDays)) || (r.Answer == "later" && r.Until > context.Today)));

    // The brief runs every day (weekends included), so the previous brief day is yesterday.
    private static bool AreaShownYesterday(IdeaSuggestion s, List<IdeaRow> shown, DateOnly today) =>
        shown.Any(r => r.Date == today.AddDays(-1) && r.Area == s.Area)
        && !(s.Deadline is { } deadline && deadline <= today.AddDays(7));

    private static bool TextWithheld(IdeaSuggestion s, SecretPatterns secrets) =>
        new[] { s.Text, s.WhyNow, s.Prepare ?? string.Empty }.Concat(s.Basis.Select(b => b.Line)).Concat(s.Basis.Select(b => b.File))
            .Any(text => MailRunValidator.WithheldReason(text, secrets) is not null);

    [GeneratedRegex(@"^inbox/github-inventory-[^/]*\.md$", RegexOptions.CultureInvariant)]
    private static partial Regex GithubInventory();
}
