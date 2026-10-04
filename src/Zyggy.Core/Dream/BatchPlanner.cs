using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Zyggy.Core.Dream;

/// <summary>
/// Picks the next batch of unconsumed lines (AC-9): <c>[stated]</c> inbox lines (oldest file date first), then <c>daily/</c> lines,
/// then the other inbox lines (oldest file date, then file name, then line order), within the line and byte caps.
/// </summary>
internal static partial class BatchPlanner
{
    public static DreamBatch? Plan(MemorySnapshot snapshot, DreamLedger ledger, IReadOnlySet<string> quarantined, int maxLines, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(quarantined);

        var candidates = new List<Candidate>();
        foreach (var file in snapshot.Files.Values)
        {
            var source = Source(file.RelativePath);
            if (source is null)
            {
                continue;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (number, text) in MemorySnapshot.BodyLines(file.Text))
            {
                var hash = LineHash.Of(text);
                if (!seen.Add(hash) || ledger.IsConsumed(file.RelativePath, hash) || quarantined.Contains(hash))
                {
                    continue;
                }

                var lineClass = source.Value.Daily
                    ? DreamLineClass.Daily
                    : text.StartsWith("- [stated]", StringComparison.Ordinal) ? DreamLineClass.StatedInbox : DreamLineClass.ObservedInbox;
                candidates.Add(new Candidate(file.RelativePath, text.TrimEnd(), hash, lineClass, source.Value.Date, number));
            }
        }

        var ordered = candidates
            .OrderBy(c => c.Class)
            .ThenBy(c => c.FileDate ?? DateOnly.MaxValue)
            .ThenBy(c => c.RelativePath, StringComparer.Ordinal)
            .ThenBy(c => c.LineNumber);

        var lines = new List<DreamBatchLine>();
        var bytes = 0;
        foreach (var candidate in ordered)
        {
            var size = Encoding.UTF8.GetByteCount(candidate.Text) + 1;
            if (lines.Count >= maxLines || (lines.Count > 0 && bytes + size > maxBytes))
            {
                break;
            }

            lines.Add(new DreamBatchLine($"L{lines.Count + 1}", candidate.RelativePath, candidate.Text, candidate.Hash, candidate.Class,
                candidate.FileDate, candidate.LineNumber));
            bytes += size;
            if (bytes > maxBytes)
            {
                break; // a single oversized first line is offered alone (Assumption 6)
            }
        }

        return lines.Count == 0 ? null : new DreamBatch(lines);
    }

    /// <summary>Whether a file offers lines, whether it is a daily file, and its date.</summary>
    internal static (bool Daily, DateOnly? Date)? Source(string relativePath)
    {
        var segments = relativePath.Split('/');
        if (segments.Length != 2 || !segments[1].EndsWith(".md", StringComparison.Ordinal) || segments[1].StartsWith('_'))
        {
            return null;
        }

        if (segments[0] == "daily")
        {
            return DailyName().Match(segments[1]) is { Success: true } day ? (true, ParseDate(day.Groups[1].Value)) : null;
        }

        if (segments[0] == "inbox")
        {
            return (false, InboxDate().Match(segments[1]) is { Success: true } dated ? ParseDate(dated.Groups[1].Value) : null);
        }

        return null;
    }

    private static DateOnly? ParseDate(string text) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    [GeneratedRegex(@"^(\d{4}-\d{2}-\d{2})\.md$", RegexOptions.CultureInvariant)]
    private static partial Regex DailyName();

    [GeneratedRegex(@"-(\d{4}-\d{2}-\d{2})\.md$", RegexOptions.CultureInvariant)]
    private static partial Regex InboxDate();

    private sealed record Candidate(string RelativePath, string Text, string Hash, DreamLineClass Class, DateOnly? FileDate, int LineNumber);
}
