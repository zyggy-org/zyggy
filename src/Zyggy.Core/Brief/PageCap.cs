namespace Zyggy.Core.Brief;

/// <summary>What a rendered line is, for the page cap.</summary>
internal enum LineKind
{
    Fixed,
    MailUrgent,
    MailImportant,
    File,
    ZItem,
    YouItem,
    Idea,
}

/// <summary>A rendered line with what the page cap needs to know about it.</summary>
internal sealed record RenderedLine(string Text, LineKind Kind, bool Urgent = false, int Order = 0);

/// <summary>
/// The one-page rule (spec 35 OD-6, AC-63, AC-68): the page is at most <c>page_max_lines</c> lines and <c>page_max_chars</c> characters.
/// Shortening, only until both hold and never further: important Mail lines (least recent first), then file lines (oldest first), then
/// "Only you" and "I can do" items from the end — never an urgent Mail line, never an item from an urgent mail, never "For the long run".
/// Dropped lines stay in the file behind <see cref="BriefPayload.DropMarker"/>; count lines carry <see cref="BriefPayload.CountMarker"/>.
/// </summary>
internal static class PageCap
{
    public static (IReadOnlyList<string> Lines, bool PageExceeded) Apply(IReadOnlyList<RenderedLine> rendered, int maxLines, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(rendered);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLines, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, 1);
        var lines = rendered.Select(l => new Slot(l)).ToList();
        if (Fits(lines, maxLines, maxChars))
        {
            return (lines.Select(s => s.Line.Text).ToList(), false);
        }

        // 1. important Mail lines, least recent first.
        Shorten(lines, maxLines, maxChars, LineKind.MailImportant, n => $"- … and {n} more important mail{(n == 1 ? string.Empty : "s")} — say \"brief full\"");
        // 2. file lines, oldest first.
        Shorten(lines, maxLines, maxChars, LineKind.File, n => $"- … and {n} more file{(n == 1 ? string.Empty : "s")} — say \"brief full\"");
        // 3. items beyond the page, from the end: "Only you" then "I can do", never an item from an urgent mail.
        Shorten(lines, maxLines, maxChars, LineKind.YouItem, n => $"… and {n} more — say \"brief full\"");
        Shorten(lines, maxLines, maxChars, LineKind.ZItem, n => $"… and {n} more — say \"brief full\"");

        var exceeded = !Fits(lines, maxLines, maxChars);
        var result = new List<string>(lines.Count);
        foreach (var s in lines)
        {
            result.Add(s.Dropped ? BriefPayload.DropMarker + s.Line.Text : s.Line.Text);
            if (s.CountLine is not null)
            {
                result.Add(BriefPayload.CountMarker + s.CountLine);
            }
        }

        return (result, exceeded);
    }

    private static void Shorten(List<Slot> lines, int maxLines, int maxChars, LineKind kind, Func<int, string> countLine)
    {
        var droppable = lines.Where(s => s.Line.Kind == kind && !s.Line.Urgent).ToList();
        if (droppable.Count == 0)
        {
            return;
        }

        // Drop order: least recent first for mails (they are listed oldest first, so from the start), oldest first for files (from the
        // start), from the end for items.
        IEnumerable<Slot> order = kind switch
        {
            LineKind.MailImportant => droppable.OrderBy(s => s.Line.Order),
            LineKind.File => droppable.OrderBy(s => s.Line.Order),
            _ => droppable.AsEnumerable().Reverse(),
        };
        var dropped = 0;
        Slot? anchor = null;
        foreach (var slot in order)
        {
            if (Fits(lines, maxLines, maxChars))
            {
                break;
            }

            slot.Dropped = true;
            dropped++;
            anchor ??= slot;
            // The count line sits where the section's last kept line is (or where the first dropped one was).
            foreach (var s in lines)
            {
                s.CountLine = s.CountLine is not null && s.Line.Kind == kind ? null : s.CountLine;
            }

            var lastKept = lines.LastOrDefault(s => s.Line.Kind == kind && !s.Dropped) ?? lines.First(s => s.Line.Kind == kind);
            lastKept.CountLine = countLine(dropped);
        }

    }

    private static bool Fits(List<Slot> lines, int maxLines, int maxChars)
    {
        var count = 0;
        var chars = 0;
        foreach (var s in lines)
        {
            if (!s.Dropped)
            {
                count++;
                chars += s.Line.Text.Length + 1;
            }

            if (s.CountLine is not null)
            {
                count++;
                chars += s.CountLine.Length + 1;
            }
        }

        return count <= maxLines && chars <= maxChars;
    }

    private sealed class Slot(RenderedLine line)
    {
        public RenderedLine Line { get; } = line;

        public bool Dropped { get; set; }

        public string? CountLine { get; set; }
    }
}
