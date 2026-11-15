using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Bus;

/// <content>The read side of <c>zyggy work</c> (deliverable 18): live sessions and open jobs per machine, over the same checkout.</content>
public sealed partial class BusInventory
{
    private static readonly TimeSpan WorkingFor = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan SilentAfter = TimeSpan.FromMinutes(60);

    /// <summary>
    /// What every machine of the tenant is doing: the sessions whose status verifies and was written by the folder's machine to
    /// Central, the jobs without a report, and the newest report. A status that fails is counted, never shown.
    /// </summary>
    /// <param name="workMachines">Machines whose statuses are returned with the four fixed fields only (founding spec §8).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The board.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="workMachines"/> is null.</exception>
    /// <exception cref="BusBusyException">Another zyggy process held the checkout's write lock for the whole wait.</exception>
    public async Task<WorkBoard> WorkAsync(IReadOnlyCollection<MachineName> workMachines, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workMachines);
        using IDisposable handle = await LockAsync(ct).ConfigureAwait(false);
        DateTimeOffset now = _clock.GetUtcNow();
        var machines = new List<MachineWork>();
        int notShown = 0;
        foreach (MachineName machine in Machines())
        {
            var sessions = new List<WorkSession>();
            foreach ((string path, EnvelopeId id) in Envelopes(BusPaths.Status(_me.Tenant, machine)))
            {
                (_, Envelope.Envelope? verified) = await SummariseAsync(path, id, ct).ConfigureAwait(false);
                if (verified?.Status is not { } status || verified.Header.From != machine || verified.Header.To != MachineName.Central || verified.Header.Id != id)
                {
                    notShown++;
                    continue;
                }

                StatusFields shown = workMachines.Contains(machine) ? new StatusFields(status.Project, status.Session, status.Since, status.Activity) : status;
                sessions.Add(new WorkSession(Classify(now, verified.Header.Created, status.Activity), shown));
            }

            var jobs = new List<WorkJob>();
            foreach ((string folder, bool claimed) in new[] { (BusPaths.Claimed(_me.Tenant, machine), true), (BusPaths.Jobs(_me.Tenant, machine), false) })
            {
                foreach ((string path, EnvelopeId id) in Envelopes(folder).OrderBy(e => e.Id.Value, StringComparer.Ordinal))
                {
                    (EnvelopeSummary summary, _) = await SummariseAsync(path, id, ct).ConfigureAwait(false);
                    jobs.Add(new WorkJob(id, summary.Verified ? summary.Project : null, claimed, id.Timestamp));
                }
            }

            EnvelopeSummary? lastReport = null;
            if (Envelopes(BusPaths.Reports(_me.Tenant, machine)).OrderBy(e => e.Id.Value, StringComparer.Ordinal).LastOrDefault() is { Path: not null } newest)
            {
                (lastReport, _) = await SummariseAsync(newest.Path, newest.Id, ct).ConfigureAwait(false);
            }

            if (sessions.Count > 0 || jobs.Count > 0 || lastReport is not null)
            {
                machines.Add(new MachineWork(machine, [.. sessions.OrderByDescending(s => s.Status.Activity)], jobs, lastReport));
            }
        }

        return new WorkBoard(now, machines, notShown);
    }

    /// <summary>Silent when the status file is 60 minutes old or more; otherwise working while the last turn ended less than 10 minutes ago, else idle.</summary>
    internal static SessionActivity Classify(DateTimeOffset now, DateTimeOffset written, DateTimeOffset activity) =>
        now - written >= SilentAfter ? SessionActivity.Silent
        : now - activity < WorkingFor ? SessionActivity.Working
        : SessionActivity.Idle;
}
