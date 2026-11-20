using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Supervision;

/// <summary>What kind of thing waits for the owner.</summary>
internal enum OwnerItemKind
{
    /// <summary>A question Central escalated.</summary>
    Question,

    /// <summary>A report Central escalated.</summary>
    Report,

    /// <summary>A paused mandate.</summary>
    Mandate,
}

/// <summary>One item that waits for the owner: ids, kinds and names only, never a text an agent wrote.</summary>
/// <param name="Key">The stable name of the item; a mail names a key once.</param>
/// <param name="Kind">Question, report or mandate.</param>
/// <param name="Machine">The machine concerned.</param>
/// <param name="Project">The project, never for a work machine.</param>
/// <param name="Id">The id of the question, report or mandate.</param>
/// <param name="Since">Since when it waits.</param>
internal sealed record OwnerItem(string Key, OwnerItemKind Kind, MachineName Machine, string? Project, EnvelopeId Id, DateTimeOffset Since);

/// <summary>
/// The pure waiting list (deliverable 18, "Waiting list"). An item waits when the newest decision on an event is <c>escalate</c>, or a
/// mandate is paused. A paused mandate is one item: the node's own <c>escalate</c> on the report that paused it, and any escalate on a
/// report of a mandate that is paused or has been resumed since, are not listed again. An item of a work machine never names a project.
/// </summary>
internal static class OwnerView
{
    /// <summary>Lists what waits for the owner, oldest first.</summary>
    /// <param name="decisions">Central's decisions, live and archived, in any order.</param>
    /// <param name="ledgers">Every mandate's ledger.</param>
    /// <param name="reports">The ids of the reports on the bus; the ids of the ledgers' reports count without being listed.</param>
    /// <param name="isWorkMachine">Whether a machine is a work machine.</param>
    /// <returns>The items.</returns>
    public static IReadOnlyList<OwnerItem> Items(
        IReadOnlyList<DecisionRecord> decisions,
        IReadOnlyList<MandateLedger> ledgers,
        IReadOnlySet<EnvelopeId> reports,
        Func<MachineName, bool> isWorkMachine)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(ledgers);
        ArgumentNullException.ThrowIfNull(reports);
        ArgumentNullException.ThrowIfNull(isWorkMachine);
        var items = new List<OwnerItem>();
        foreach (IGrouping<EnvelopeId, DecisionRecord> group in decisions.GroupBy(d => d.InReplyTo))
        {
            DecisionRecord newest = group.OrderByDescending(d => d.Id.Value, StringComparer.Ordinal).First();
            if (newest.Fields.Kind != DecisionKind.Escalate)
            {
                continue;
            }

            MandateLedger? owner = ledgers.FirstOrDefault(l => l.Reports.Any(r => r.Id == newest.InReplyTo));
            if (owner is not null && (owner.Paused || owner.Resumed > newest.Created))
            {
                continue;
            }

            bool report = owner is not null || reports.Contains(newest.InReplyTo);
            items.Add(new OwnerItem(
                (report ? "report:" : "question:") + newest.InReplyTo.Value,
                report ? OwnerItemKind.Report : OwnerItemKind.Question,
                newest.To,
                isWorkMachine(newest.To) ? null : newest.Fields.Project,
                newest.InReplyTo,
                newest.Created));
        }

        foreach (MandateLedger ledger in ledgers)
        {
            if (ledger is { Paused: true, Revoked: false, Mandate: { Mandate: { } fields } mandate })
            {
                MandateReportEntry third = SupervisorSummary.PauseReports(ledger)[^1];
                items.Add(new OwnerItem(
                    "mandate:" + ledger.Id.Value + ":" + third.Id.Value,
                    OwnerItemKind.Mandate,
                    mandate.Header.To,
                    isWorkMachine(mandate.Header.To) ? null : fields.Project,
                    ledger.Id,
                    third.Created));
            }
        }

        return [.. items.OrderBy(i => i.Since).ThenBy(i => i.Key, StringComparer.Ordinal)];
    }
}
