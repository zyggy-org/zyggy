using System.Globalization;
using System.Text;

using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Bus.Probe;

/// <summary>One row of <c>zyggy probe stats</c>: the ledger aggregated for one target machine (milliseconds, nearest-rank percentiles over the <c>done</c> probes).</summary>
/// <param name="To">The target machine.</param>
/// <param name="Count">Probes sent.</param>
/// <param name="Done">Probes answered <c>done</c>.</param>
/// <param name="Rejected">Probes answered <c>rejected</c>.</param>
/// <param name="Timeout">Probes without a report in time.</param>
/// <param name="PushFailed">Probes that could not be pushed.</param>
/// <param name="P50">The median round trip, when any probe was done.</param>
/// <param name="P95">The 95th percentile round trip.</param>
/// <param name="Min">The shortest round trip.</param>
/// <param name="Max">The longest round trip.</param>
public sealed record ProbeStatsRow(MachineName To, int Count, int Done, int Rejected, int Timeout, int PushFailed, long? P50, long? P95, long? Min, long? Max);

/// <summary>Aggregates the probe ledger per target (deliverable 04 CLI table: <c>zyggy probe stats</c>); the table is pasted into the transport decision record.</summary>
public static class ProbeStats
{
    /// <summary>Aggregates <paramref name="records"/> per target machine, in machine-name order.</summary>
    /// <param name="records">The ledger records.</param>
    /// <param name="to">Only this target when set.</param>
    /// <returns>One row per target; empty when nothing matches.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="records"/> is null.</exception>
    public static IReadOnlyList<ProbeStatsRow> Aggregate(IEnumerable<ProbeRecord> records, MachineName? to = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        return records
            .Where(r => to is null || r.To == to)
            .GroupBy(r => r.To.Value, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var rows = g.ToList();
                var times = rows.Where(r => r.Outcome == ProbeOutcome.Done && r.RoundTripMs is not null).Select(r => r.RoundTripMs!.Value).Order().ToList();
                return new ProbeStatsRow(
                    MachineName.Parse(g.Key),
                    rows.Count,
                    rows.Count(r => r.Outcome == ProbeOutcome.Done),
                    rows.Count(r => r.Outcome == ProbeOutcome.Rejected),
                    rows.Count(r => r.Outcome == ProbeOutcome.Timeout),
                    rows.Count(r => r.Outcome == ProbeOutcome.PushFailed),
                    Percentile(times, 50),
                    Percentile(times, 95),
                    times.Count == 0 ? null : times[0],
                    times.Count == 0 ? null : times[^1]);
            })
            .ToList();
    }

    /// <summary>Renders the rows as a Markdown table with the columns <c>to | count | done | rejected | timeout | push_failed | p50 ms | p95 ms | min ms | max ms</c>.</summary>
    /// <param name="rows">The rows.</param>
    /// <returns>The table text, ending with a newline.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="rows"/> is null.</exception>
    public static string RenderMarkdown(IEnumerable<ProbeStatsRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var sb = new StringBuilder();
        sb.Append("| to | count | done | rejected | timeout | push_failed | p50 ms | p95 ms | min ms | max ms |\n");
        sb.Append("|----|------:|-----:|---------:|--------:|------------:|-------:|-------:|-------:|-------:|\n");
        foreach (ProbeStatsRow row in rows)
        {
            sb.Append(CultureInfo.InvariantCulture, $"| {row.To.Value} | {row.Count} | {row.Done} | {row.Rejected} | {row.Timeout} | {row.PushFailed} | {Cell(row.P50)} | {Cell(row.P95)} | {Cell(row.Min)} | {Cell(row.Max)} |\n");
        }

        return sb.ToString();
    }

    /// <summary>Nearest-rank percentile over sorted values: the value at rank ⌈p/100 × n⌉ (1-based).</summary>
    private static long? Percentile(List<long> sorted, int percentile)
    {
        if (sorted.Count == 0)
        {
            return null;
        }

        int rank = (int)Math.Ceiling(percentile / 100.0 * sorted.Count);
        return sorted[Math.Clamp(rank, 1, sorted.Count) - 1];
    }

    private static string Cell(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "-";
}
