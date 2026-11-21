using Zyggy.Core.Envelope;
using Zyggy.Core.Supervision;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Bus;

/// <content>The read side of the supervisor wake (deliverable 18, "The supervisor run").</content>
public sealed partial class BusInventory
{
    /// <summary>
    /// The events this checkout holds for a supervisor run: every verified question to Central in its writer's own <c>questions/</c>
    /// folder, and every verified report to Central that carries a mandate, in its writer's <c>reports/</c> folder or in the archive
    /// (Central's pickup archives such a report before the wake looks). Each with its mandate's state at <paramref name="now"/> and
    /// whether a decision of Central already replies to it. Read from the bus only, so a restart sees the same events.
    /// </summary>
    internal async Task<IReadOnlyList<SupervisorEvent>> SupervisorEventsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var found = new List<(EnvelopeId Id, SupervisorEventKind Kind, MachineName Machine, EnvelopeId? Mandate)>();
        var handled = new HashSet<string>(StringComparer.Ordinal);
        using (await LockAsync(ct).ConfigureAwait(false))
        {
            var reportFiles = new List<(string Path, EnvelopeId Id, MachineName? Folder)>();
            foreach (MachineName machine in Machines())
            {
                foreach ((string path, EnvelopeId id) in Envelopes(BusPaths.Questions(_me.Tenant, machine)))
                {
                    if (await VerifiedForCentralAsync(path, id, machine, ct).ConfigureAwait(false) is { Question: not null })
                    {
                        found.Add((id, SupervisorEventKind.Question, machine, null));
                    }
                }

                reportFiles.AddRange(Envelopes(BusPaths.Reports(_me.Tenant, machine)).Select(f => (f.Path, f.Id, (MachineName?)machine)));
            }

            foreach (string month in ArchiveMonths.Folders(_root, _me.Tenant))
            {
                reportFiles.AddRange(Envelopes(month).Select(f => (f.Path, f.Id, (MachineName?)null)));
            }

            foreach ((string path, EnvelopeId id, MachineName? folder) in reportFiles)
            {
                if (EnvelopeParser.PeekHeader(await File.ReadAllBytesAsync(BusPaths.Resolve(_root, path), ct).ConfigureAwait(false))?.Type == EnvelopeType.Report
                    && await VerifiedForCentralAsync(path, id, folder, ct).ConfigureAwait(false) is { Report.Mandate: { } mandate } report)
                {
                    found.Add((id, SupervisorEventKind.Report, report.Header.From, mandate));
                }
            }

            foreach ((string path, EnvelopeId id, bool archived) in DecisionFiles())
            {
                if (await ReadDecisionAsync(path, id, archived, ct).ConfigureAwait(false) is { } decision)
                {
                    handled.Add(decision.InReplyTo.Value);
                }
            }
        }

        // The ledger takes the lock itself; read each mandate once, after it is released.
        var states = new Dictionary<string, MandateState>(StringComparer.Ordinal);
        foreach (EnvelopeId mandate in found.Where(f => f.Mandate is not null).Select(f => f.Mandate!).DistinctBy(m => m.Value))
        {
            states[mandate.Value] = (await MandateLedgerAsync(mandate, ct).ConfigureAwait(false)).StateAt(now);
        }

        return [.. found
            .OrderBy(f => f.Id.Value, StringComparer.Ordinal)
            .Select(f => new SupervisorEvent(f.Id, f.Kind, f.Machine, f.Mandate, f.Mandate is null ? null : states[f.Mandate.Value], handled.Contains(f.Id.Value)))];
    }

    /// <summary>
    /// What waits for the owner (deliverable 18, "Waiting list"), oldest first: an event whose newest decision is <c>escalate</c>, and a
    /// paused mandate. Ids, kinds and names only; an item of a work machine names no project.
    /// </summary>
    /// <param name="workMachines">The machines in <c>supervision.workMachines</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The items.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="workMachines"/> is null.</exception>
    /// <exception cref="BusBusyException">Another zyggy process held the checkout's write lock for the whole wait.</exception>
    public async Task<IReadOnlyList<OwnerItem>> WaitingAsync(IReadOnlyList<MachineName> workMachines, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workMachines);

        // Each read takes the lock itself; the report ids decide whether an escalated event is a report or a question.
        IReadOnlyList<DecisionRecord> decisions = await DecisionsAsync(null, null, ct).ConfigureAwait(false);
        IReadOnlyList<MandateLedger> ledgers = await MandatesAsync(ct).ConfigureAwait(false);
        var reports = new HashSet<EnvelopeId>();
        using (await LockAsync(ct).ConfigureAwait(false))
        {
            foreach (MachineName machine in Machines())
            {
                reports.UnionWith(Envelopes(BusPaths.Reports(_me.Tenant, machine)).Select(f => f.Id));
            }

            foreach (string month in ArchiveMonths.Folders(_root, _me.Tenant))
            {
                foreach ((string path, EnvelopeId id) in Envelopes(month))
                {
                    if (EnvelopeParser.PeekHeader(await File.ReadAllBytesAsync(BusPaths.Resolve(_root, path), ct).ConfigureAwait(false))?.Type == EnvelopeType.Report)
                    {
                        reports.Add(id);
                    }
                }
            }
        }

        return OwnerView.Items(decisions, ledgers, reports, workMachines.Contains);
    }

    /// <summary>
    /// The envelope at <paramref name="path"/> when it verifies under its own id, is addressed to Central and, when
    /// <paramref name="folder"/> is given, was written by the machine whose folder holds it.
    /// </summary>
    private async Task<Envelope.Envelope?> VerifiedForCentralAsync(string path, EnvelopeId id, MachineName? folder, CancellationToken ct)
    {
        (_, Envelope.Envelope? verified) = await SummariseAsync(path, id, ct).ConfigureAwait(false);
        return verified is not null
            && verified.Header.Id == id
            && verified.Header.To == MachineName.Central
            && (folder is null || verified.Header.From == folder)
            ? verified
            : null;
    }
}
