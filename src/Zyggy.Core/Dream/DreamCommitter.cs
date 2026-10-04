using System.Globalization;
using System.Text;

using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Dream;

/// <summary>
/// The commit of a run (spec 28 AC-19): subject <c>dream YYYY-MM-DD</c> (the run's local start date), a body whose first line names
/// the outcome followed by plain counts, and the trailers <c>Zyggy-Run</c> and <c>Zyggy-Trigger</c>; and exactly the run's paths,
/// never <c>inbox/</c> or the pending marker.
/// </summary>
internal static class DreamCommitter
{
    public static string Message(DreamRunRecord record, DateOnly runDate)
    {
        ArgumentNullException.ThrowIfNull(record);
        var batches = record.Batches;
        var outcome = record.Outcome
            + (record.Check is { } check ? $" (check {check})" : string.Empty)
            + (record.Reason is { } reason ? $" ({reason}{(record.Detail is { } d ? ": " + d : string.Empty)})" : string.Empty);
        var text = new StringBuilder();
        var c = CultureInfo.InvariantCulture;
        text.Append(c, $"dream {runDate:yyyy-MM-dd}\n\n");
        if (record.Migrated > 0)
        {
            text.Append(c, $"layout migrated\nmoves: {record.Migrated}; withheld: {record.Withheld.Count}; carried: {record.Carried.Count}\n");
            text.Append(c, $"cost: {record.CostUsdTotal:0.00} USD\n\n");
            text.Append(c, $"Zyggy-Run: {record.Run}\n");
            text.Append(c, $"Zyggy-Trigger: {record.Trigger}\n");
            return text.ToString();
        }

        text.Append(c, $"outcome: {outcome}\n");
        text.Append(c, $"batches: {batches.Count} ({batches.Count(b => b.Result == "accepted")} accepted), lines: {batches.Sum(b => b.Lines)}, ");
        text.Append(c, $"filed: {batches.Sum(b => b.Filed)}, merged: {batches.Sum(b => b.Merged)}, duplicate: {batches.Sum(b => b.Duplicate)}, ");
        text.Append(c, $"dropped: {batches.Sum(b => b.Dropped.Values.Sum())}\n");
        text.Append(c, $"files: {batches.Sum(b => b.FilesCreated)} created, {batches.Sum(b => b.FilesEdited)} edited; ");
        text.Append(c, $"categories created: {batches.Sum(b => b.CategoriesCreated)}; compressions: {record.Compressions.Count(x => x.Result == "accepted")}; ");
        text.Append(c, $"quarantined lines: {record.Quarantined}\n");
        text.Append(c, $"rollup: {record.Rollup.DailyRolled} daily files rolled, {record.Rollup.InboxDeleted} inbox files deleted; withheld: {record.Withheld.Count}; carried: {record.Carried.Count}\n");
        text.Append(c, $"cost: {record.CostUsdTotal:0.00} USD\n\n");
        text.Append(c, $"Zyggy-Run: {record.Run}\n");
        text.Append(c, $"Zyggy-Trigger: {record.Trigger}\n");
        return text.ToString();
    }

    /// <summary>The run's paths relative to the repository root, ordinal order; <c>inbox/</c> and the pending marker are never included.</summary>
    public static IReadOnlyList<string> RepoPaths(Principal principal, IEnumerable<string> relativePaths)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return relativePaths
            .Where(p => !p.StartsWith("inbox/", StringComparison.Ordinal) && p != ".dream/pending.json")
            .Select(p => $"{principal.Tenant.Value}/{principal.User.Value}/{p}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
    }
}
