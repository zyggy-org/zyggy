using System.Globalization;
using System.Text;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;

namespace Zyggy.Core.Supervision;

/// <summary>
/// The text of the decisions Central's node writes itself, without a model (deliverable 18, "Written by the node"). Built from ids,
/// step labels, kinds and counts only: nothing an agent wrote as free text reaches it.
/// </summary>
internal static class SupervisorSummary
{
    /// <summary>The cap of a decision's body (deliverable 18, "Questions and decisions").</summary>
    public const int MaxLength = 1200;

    private const int AcceptedBudget = 700;

    private const int WaitingBudget = 300;

    private const int PauseAfter = 3;

    /// <summary>
    /// The body of the <c>stop</c> decision on a mandate that is exhausted, expired or revoked: the jobs used, the steps accepted
    /// with their evidence ids, the count of decisions per kind and the ids of what waits for the owner. Only decisions that act
    /// under the mandate are read. A list that does not fit is cut with "and n more".
    /// </summary>
    public static string Stop(MandateState state, MandateLedger ledger, IReadOnlyList<DecisionRecord> decisions)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(decisions);
        DecisionRecord[] own = [.. decisions.Where(d => d.Fields.Mandate == ledger.Id).OrderBy(d => d.Id.Value, StringComparer.Ordinal)];
        IEnumerable<string> accepted = own
            .Where(d => d.Fields.Kind == DecisionKind.Accept)
            .Select(d => (d.Fields.Step ?? d.InReplyTo.Value) + (d.Fields.Evidence is { Count: > 0 } evidence ? " (" + string.Join(", ", evidence) + ")" : string.Empty));
        IEnumerable<string> kinds = own
            .GroupBy(d => EnvelopeWire.ToWire(d.Fields.Kind), StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => string.Create(CultureInfo.InvariantCulture, $"{g.Key} {g.Count()}"));
        IEnumerable<string> waiting = own
            .GroupBy(d => d.InReplyTo.Value, StringComparer.Ordinal)
            .Where(g => g.MaxBy(d => d.Id.Value, StringComparer.Ordinal)!.Fields.Kind == DecisionKind.Escalate)
            .Select(g => g.Key)
            .Order(StringComparer.Ordinal);
        string max = ledger.Mandate?.Mandate is { } fields ? fields.MaxJobs.ToString(CultureInfo.InvariantCulture) : "?";
        var text = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"mandate {StateName(state)}: {ledger.Counted.Count} of {max} jobs used\n")
            .Append("accepted: ").Append(Listing([.. accepted], "; ", "none", AcceptedBudget)).Append('\n')
            .Append("decisions: ").Append(Listing([.. kinds], ", ", "none", int.MaxValue)).Append('\n')
            .Append("waiting for the owner: ").Append(Listing([.. waiting], ", ", "nothing", WaitingBudget));
        return text.Length <= MaxLength ? text.ToString() : text.ToString(0, MaxLength);
    }

    /// <summary>
    /// The body of the <c>escalate</c> decision on the third failing report of a paused mandate: the three reports with their status
    /// and reason, and the command that resumes it.
    /// </summary>
    public static string Paused(MandateLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        IEnumerable<string> reports = PauseReports(ledger)
            .Select(r => r.Id.Value + " " + EnvelopeWire.ToWire(r.Status) + (r.Reason is { Length: > 0 } reason ? " " + reason : string.Empty));
        return "mandate paused: its three newest reports are not done (" + string.Join(", ", reports) + "); resume with: zyggy mandate resume " + ledger.Id.Value;
    }

    /// <summary>The three newest reports since the last resume, oldest first: the reports that paused the mandate.</summary>
    public static IReadOnlyList<MandateReportEntry> PauseReports(MandateLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        return [.. ledger.Reports
            .Where(r => ledger.Resumed is null || r.Created > ledger.Resumed)
            .OrderByDescending(r => r.Id.Value, StringComparer.Ordinal)
            .Take(PauseAfter)
            .Reverse()];
    }

    private static string StateName(MandateState state) => state switch
    {
        MandateState.Exhausted => "exhausted",
        MandateState.Expired => "expired",
        MandateState.Revoked => "revoked",
        _ => "closed",
    };

    // The items joined while they fit the budget; what does not fit becomes "and n more".
    private static string Listing(IReadOnlyList<string> items, string separator, string none, int budget)
    {
        if (items.Count == 0)
        {
            return none;
        }

        var text = new StringBuilder();
        for (var i = 0; i < items.Count; i++)
        {
            string more = string.Create(CultureInfo.InvariantCulture, $"{separator}and {items.Count - i} more");
            if (i > 0 && text.Length + separator.Length + items[i].Length + more.Length > budget)
            {
                return text.Append(more).ToString();
            }

            text.Append(i > 0 ? separator : string.Empty).Append(items[i]);
        }

        return text.ToString();
    }
}
