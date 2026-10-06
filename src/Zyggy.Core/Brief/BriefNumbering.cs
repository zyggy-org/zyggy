namespace Zyggy.Core.Brief;

/// <summary>A Z item before it has its number.</summary>
internal sealed record ZCandidate(ZKind Kind, string? MessageId, IReadOnlyList<string>? MessageIds, string? DraftId, ZDestination? Destination, string Subject, ZSender Sender, string Received, string Why, MailClass Class, int Order);

/// <summary>
/// Z numbers are the binary's (spec 35 AC-17): urgent mails first, then important, then the discard items, then the one <c>file-other</c>
/// item; restarted every day; at most <c>suggestion_cap</c>, and the <c>file-other</c> item always fits (the cap applies to the ones before it).
/// </summary>
internal static class BriefNumbering
{
    public static (IReadOnlyList<ZItem> Items, IReadOnlyList<ZCandidate> Dropped) Number(IEnumerable<ZCandidate> candidates, int suggestionCap)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var all = candidates.ToList();
        var fileOther = all.FirstOrDefault(c => c.Kind == ZKind.FileOther);
        var ordered = all.Where(c => c.Kind != ZKind.FileOther)
            .OrderBy(c => c.Kind == ZKind.DiscardDraft ? 1 : 0)
            .ThenBy(c => c.Kind == ZKind.DiscardDraft ? MailClass.Urgent : c.Class, Comparer<MailClass>.Create((a, b) => b.CompareTo(a)))
            .ThenBy(c => c.Order)
            .ToList();
        var room = Math.Max(0, suggestionCap - (fileOther is null ? 0 : 1));
        var kept = ordered.Take(room).ToList();
        var dropped = ordered.Skip(room).ToList();
        if (fileOther is not null)
        {
            kept.Add(fileOther);
        }

        var items = kept.Select((c, i) => new ZItem(i + 1, c.Kind, c.MessageId, c.MessageIds, c.DraftId, c.Destination, c.Subject, c.Sender, c.Received, c.Why, c.Class == MailClass.Urgent)).ToList();
        return (items, dropped);
    }
}
