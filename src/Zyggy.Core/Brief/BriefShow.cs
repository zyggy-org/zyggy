using System.Globalization;
using System.Text;
using System.Text.Json;

using Zyggy.Core.M365;

namespace Zyggy.Core.Brief;

/// <summary>What <c>show</c> prints and the <c>last-shown</c> date it may record afterwards.</summary>
internal sealed record ShowOutcome(string Text, DateOnly? NewLastShown);

/// <summary>
/// The <c>zyggy brief show</c> logic (spec 35 AC-12, AC-56..AC-58, AC-67): today's or a named day's brief wrapped as data, the "earlier
/// briefs not shown" line, the failure or not-ready line when there is no brief, and the technical cap. Pure over the store and the clock.
/// </summary>
internal sealed class BriefShow(BriefStore store, BriefSettings settings, TimeZoneInfo zone, TimeProvider clock, M365Paths m365)
{
    public ShowOutcome Decide(DateOnly? requested, bool full)
    {
        var nowLocal = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone);
        var today = DateOnly.FromDateTime(nowLocal.DateTime);
        var lastShown = store.ReadLastShown();
        var dates = store.BriefDates();

        if (requested is { } date)
        {
            if (!dates.Contains(date))
            {
                return new ShowOutcome($"no brief for {BriefPaths.Iso(date)}\n", null);
            }

            var newLast = lastShown is { } shown && shown > date ? shown : date;
            return new ShowOutcome(Render(date, full), newLast);
        }

        var earlier = EarlierLine(dates, lastShown, today);
        if (dates.Contains(today))
        {
            return new ShowOutcome(Render(today, full) + earlier, today);
        }

        if (TimeOnly.FromDateTime(nowLocal.DateTime) < settings.ExpectBy)
        {
            return new ShowOutcome($"today's brief is not ready yet (expected by {settings.ExpectBy.ToString("HH:mm", CultureInfo.InvariantCulture)})\n", null);
        }

        return new ShowOutcome(FailureLine(today) + earlier, null);
    }

    private string Render(DateOnly date, bool full)
    {
        var markdown = store.ReadMarkdown(date);
        var view = full ? BriefPayload.Full(markdown) : BriefPayload.Page(markdown);
        var wrapped = BriefPayload.Wrap(date, store.Generated(date), view, Watermark());
        return BriefPayload.Fit(wrapped, date);
    }

    private static string EarlierLine(IReadOnlyList<DateOnly> dates, DateOnly? lastShown, DateOnly today)
    {
        var notShown = dates.Where(d => d < today && (lastShown is null || d > lastShown)).ToList();
        if (notShown.Count == 0)
        {
            return string.Empty;
        }

        var list = string.Join(", ", notShown.Select(BriefPaths.Iso));
        return $"{notShown.Count} earlier brief{(notShown.Count == 1 ? string.Empty : "s")} not shown ({list}) — say \"show <date>\"\n";
    }

    // The last row of today in m365/brief.jsonl with a non-zero exit, as 33 writes it; its error texts already name their runbook entry.
    private string FailureLine(DateOnly today)
    {
        var path = m365.StateFile("brief.jsonl");
        string? line = null;
        if (File.Exists(path))
        {
            foreach (var row in File.ReadLines(path, Encoding.UTF8))
            {
                try
                {
                    using var document = JsonDocument.Parse(row);
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("date", out var d) || d.GetString() != BriefPaths.Iso(today))
                    {
                        continue;
                    }

                    var exit = root.TryGetProperty("exit", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt32() : 0;
                    if (exit == 0)
                    {
                        continue;
                    }

                    var error = root.TryGetProperty("error", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : $"brief run exited {exit}";
                    var entry = error.Contains("runbook", StringComparison.Ordinal) ? string.Empty : " — runbook 13 \"Brief run failed\"";
                    line = $"exit {exit}: {BriefPayload.Neutralise(error)}{entry}";
                }
                catch (JsonException)
                {
                    // A row that is not JSON is not a record.
                }
            }
        }

        return (line ?? "no brief run recorded today — runbook 13 \"Brief run failed\"") + "\n";
    }

    private string? Watermark()
    {
        var path = m365.StateFile("mail-watermark");
        if (!File.Exists(path))
        {
            return null;
        }

        var text = File.ReadAllText(path).Trim();
        return text.Length is > 0 and <= 40 ? text : null;
    }
}
