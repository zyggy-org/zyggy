using Microsoft.Extensions.Logging;

using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Bus;

/// <summary>
/// The node's check of a job that names a mandate (deliverable 18, "Enforced on the node"), run on a fresh inbox job after its
/// signature verified and before the gate, the claim or any model call. It reads the mandate from this node's own bus checkout and
/// runs <see cref="MandateRules.CheckIntake"/>; a job without <c>mandate</c> passes untouched. Claims resumed after a restart are not
/// checked again: a job that is running when its mandate ends finishes and reports.
/// </summary>
internal sealed partial class MandateIntake
{
    private readonly BusInventory _inventory;
    private readonly MachineName _me;
    private readonly bool _enabled;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;
    private readonly string? _nodeDir;
    private bool _busyLogged;

    /// <summary>Creates the check.</summary>
    /// <param name="inventory">The read side of this node's checkout.</param>
    /// <param name="me">This node's machine.</param>
    /// <param name="enabled"><c>supervision.enabled</c>: when <see langword="false"/> every job under a mandate is refused.</param>
    /// <param name="clock">The clock the mandate's expiry is judged by.</param>
    /// <param name="log">The logger.</param>
    /// <param name="nodeDir">The node directory whose stop marker (<c>zyggy supervise stop</c>) refuses every job under a mandate; <see langword="null"/>: no marker is looked for.</param>
    public MandateIntake(BusInventory inventory, MachineName me, bool enabled, TimeProvider clock, ILogger log, string? nodeDir = null)
    {
        _nodeDir = nodeDir;
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _me = me ?? throw new ArgumentNullException(nameof(me));
        _enabled = enabled;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>Checks <paramref name="job"/>.</summary>
    /// <param name="job">A verified inbox job.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <paramref name="job"/> itself when it may run, a <see cref="RejectedJob"/> with <c>mandate_invalid</c> when it may not, or
    /// <see langword="null"/> when the checkout's lock stayed busy (the job waits for the next pass).
    /// </returns>
    public async Task<JobIntake?> CheckAsync(AcceptedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Job.Job?.Mandate is not { } id)
        {
            return job;
        }

        if (!_enabled)
        {
            return Refuse(job, "supervision_off");
        }

        // Deliverable 18 ("Stop"): the owner's marker in this node's folder refuses every job under a mandate until `zyggy supervise start`.
        if (_nodeDir is not null && Supervision.SupervisionSwitch.IsStopped(_nodeDir))
        {
            return Refuse(job, "supervision_stopped");
        }

        MandateLedger ledger;
        try
        {
            ledger = await _inventory.MandateLedgerAsync(id, ct).ConfigureAwait(false);
        }
        catch (BusBusyException)
        {
            if (!_busyLogged)
            {
                _busyLogged = true;
                LogBusy(job.File.Id.Value);
            }

            return null;
        }

        _busyLogged = false;
        MandateVerdict verdict = MandateRules.CheckIntake(ledger, job.Job, _me, _clock.GetUtcNow());
        return verdict.Limit is { } limit ? Refuse(job, MandateRules.ToWire(limit)) : job;
    }

    private static RejectedJob Refuse(AcceptedJob job, string limit) =>
        new(job.File, EnvelopeRejectionReason.MandateInvalid, "mandate", limit, job.Job.Header.From)
        {
            Mandate = job.Job.Job!.Mandate,
            Step = job.Job.Job.Step,
            JobType = job.Job.Job.JobType,
        };

    [LoggerMessage(Level = LogLevel.Warning, Message = "mandate check of {JobId} deferred: the bus checkout is busy")]
    private partial void LogBusy(string jobId);
}
