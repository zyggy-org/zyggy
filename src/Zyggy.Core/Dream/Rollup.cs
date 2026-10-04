using System.Globalization;
using System.Text;

using Zyggy.Core.Memory;

namespace Zyggy.Core.Dream;

/// <summary>Daily files of one month appended to <c>daily/YYYY-MM.md</c>.</summary>
internal sealed record DailyArchive(string MonthPath, string Text, IReadOnlyList<string> Days);

/// <summary>What the rollup will archive and delete.</summary>
internal sealed record RollupPlan(IReadOnlyList<DailyArchive> Archives, IReadOnlyList<string> InboxDeletions);

/// <summary>
/// The mechanical step 6 of the dream (spec 28 AC-23, AC-24; no model call): daily files older than
/// <see cref="DreamOptions.DailyRollupDays"/> whose every line is consumed are appended to their month file under a date heading
/// and deleted; closed, fully consumed inbox files past <see cref="DreamOptions.InboxDeleteGraceDays"/> are deleted. Their ledger
/// entries go with them.
/// </summary>
internal static class Rollup
{
    public static RollupPlan Plan(MemorySnapshot snapshot, DreamLedger ledger, DateOnly localToday, DateTimeOffset nowUtc, DreamOptions options)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(options);

        var days = new List<(DateOnly Date, SnapshotFile File)>();
        var inbox = new List<string>();
        foreach (var file in snapshot.Files.Values)
        {
            if (BatchPlanner.Source(file.RelativePath) is not { } source)
            {
                continue;
            }

            var hashes = MemorySnapshot.BodyLines(file.Text).Select(l => LineHash.Of(l.Text)).Distinct(StringComparer.Ordinal).ToList();
            if (source.Daily)
            {
                var date = source.Date!.Value;
                if (localToday.DayNumber - date.DayNumber > options.DailyRollupDays
                    && (hashes.Count == 0 || ledger.AllConsumed(file.RelativePath, hashes)))
                {
                    days.Add((date, file));
                }

                continue;
            }

            var quiet = nowUtc - new DateTimeOffset(DateTime.SpecifyKind(file.LastWriteUtc, DateTimeKind.Utc)) >= TimeSpan.FromHours(24);
            var closed = quiet && (source.Date is null || source.Date.Value.DayNumber <= localToday.DayNumber - 2);
            if (closed && hashes.Count > 0 && ledger.AllConsumed(file.RelativePath, hashes)
                && ledger.LastConsumed(file.RelativePath) is { } last && localToday.DayNumber - last.DayNumber >= options.InboxDeleteGraceDays)
            {
                inbox.Add(file.RelativePath);
            }
        }

        var archives = days
            .GroupBy(d => $"daily/{d.Date:yyyy-MM}.md")
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => Archive(snapshot, g.Key, g.OrderBy(d => d.Date).ToList(), localToday))
            .ToList();
        return new RollupPlan(archives, inbox.Order(StringComparer.Ordinal).ToList());
    }

    public static void Apply(RollupPlan plan, WorkingSet set, DreamLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(ledger);
        foreach (var archive in plan.Archives)
        {
            set.Write(archive.MonthPath, archive.Text);
            foreach (var day in archive.Days)
            {
                set.Delete(day);
                ledger.Remove(day);
            }
        }

        foreach (var path in plan.InboxDeletions)
        {
            set.Delete(path);
            ledger.Remove(path);
        }
    }

    /// <summary>AC-24: the only deletions a run may make are the rollup plan's.</summary>
    public static DreamCheck? CheckDeletions(WorkingSet set, RollupPlan plan)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(plan);
        var allowed = plan.Archives.SelectMany(a => a.Days).Concat(plan.InboxDeletions).ToHashSet(StringComparer.Ordinal);
        return set.ChangedPaths.Any(p => !set.Exists(p) && !allowed.Contains(p)) ? DreamCheck.UnfiledDeletion : null;
    }

    private static DailyArchive Archive(MemorySnapshot snapshot, string monthPath, List<(DateOnly Date, SnapshotFile File)> days, DateOnly localToday)
    {
        var text = new StringBuilder();
        if (snapshot.Files.TryGetValue(monthPath, out var existing))
        {
            text.Append(existing.Text);
            if (text.Length > 0 && text[^1] != '\n')
            {
                text.Append('\n');
            }
        }
        else
        {
            var month = monthPath["daily/".Length..^3];
            text.Append(CultureInfo.InvariantCulture,
                $"---\nname: daily {month}\ndescription: daily notes of {month} (archive)\nupdated: {localToday:yyyy-MM-dd}\n---\n");
        }

        foreach (var (date, file) in days)
        {
            text.Append(CultureInfo.InvariantCulture, $"## {date:yyyy-MM-dd}\n");
            foreach (var line in Body(file.Text))
            {
                text.Append(line).Append('\n');
            }
        }

        return new DailyArchive(monthPath, text.ToString(), days.Select(d => d.File.RelativePath).ToList());
    }

    private static IEnumerable<string> Body(string text)
    {
        var lines = TextLines.Split(text);
        var start = 0;
        if (lines.Count > 0 && lines[0] == "---")
        {
            var close = lines.IndexOf("---", 1);
            start = close < 0 ? lines.Count : close + 1;
        }

        return lines.Skip(start);
    }
}
