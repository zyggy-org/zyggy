using System.Text;

using Microsoft.Extensions.Logging;

using Zyggy.Core.Envelope;
using Zyggy.Core.Runs;
using Zyggy.Core.Tenancy;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Bus;

/// <summary>What <c>zyggy withdraw</c> did (spec 61 §1).</summary>
internal enum WithdrawResult
{
    /// <summary>A queued job was removed with this machine's <c>failed</c> / <c>withdrawn</c> report.</summary>
    Withdrawn,

    /// <summary>A claimed job got a signed withdrawal in its target's <c>withdrawals/</c>.</summary>
    Requested,

    /// <summary>Nothing on the bus names the id.</summary>
    NotFound,

    /// <summary>The job was already answered.</summary>
    AlreadyReported,

    /// <summary>A claimed session task: finished with <c>zyggy work --failed</c>, never withdrawn.</summary>
    SessionTask,

    /// <summary>This machine is neither the job's sender nor Central.</summary>
    NotSender,

    /// <summary>The job's file does not verify.</summary>
    DoesNotVerify,

    /// <summary>Another zyggy process held the checkout's lock.</summary>
    Busy,

    /// <summary>A git step or the signing failed.</summary>
    Failed,
}

/// <summary>The outcome of one withdrawal.</summary>
/// <param name="Result">What happened.</param>
/// <param name="Target">The machine the job was queued on or claimed by.</param>
/// <param name="Detail">The failure's detail, the reported status, or the job's sender.</param>
/// <param name="PushDeferred">Committed, but the push is deferred.</param>
/// <param name="TargetVersion">The claiming machine's registry <c>version</c> when it does not stop a running job (older than 0.30.1, or "unknown"); <see langword="null"/> otherwise.</param>
internal sealed record WithdrawOutcome(WithdrawResult Result, MachineName? Target = null, string? Detail = null, bool PushDeferred = false, string? TargetVersion = null);

/// <summary>
/// The sender side of <c>zyggy withdraw &lt;id&gt; [--why &lt;line&gt;]</c> (spec 61 §1, Q1 (b)): pulls, finds the job in every machine's
/// <c>jobs/</c> and <c>jobs/claimed/</c>, then removes a queued job with this machine's own <c>failed</c> / <c>withdrawn</c> report, or writes a
/// signed <c>withdrawal</c> for a claimed one — only for the job's sender, or Central for any job of the tenant (<see cref="WithdrawalRules"/>).
/// A queued removal that loses the race with the node's claim re-reads the state once and follows the claimed path.
/// </summary>
internal sealed partial class JobWithdrawer
{
    /// <summary>The first node version that stops a running job on a withdrawal.</summary>
    private static readonly Version StopsRunsFrom = new(0, 30, 1);

    private readonly BusRepository _bus;
    private readonly BusInventory _inventory;
    private readonly EnvelopeSigner _signer;
    private readonly KeyId _signWith;
    private readonly TimeProvider _clock;
    private readonly ILogger<JobWithdrawer> _log;

    internal JobWithdrawer(BusRepository bus, BusInventory inventory, EnvelopeSigner signer, KeyId signWith, TimeProvider clock, ILogger<JobWithdrawer> log)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _signWith = signWith ?? throw new ArgumentNullException(nameof(signWith));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Whether a node of registry <paramref name="version"/> stops a running job on a withdrawal: 0.30.1 or later (a pre-release of 0.30.1
    /// counts, build metadata is ignored); an absent or unreadable version does not.
    /// </summary>
    /// <param name="version">The registry file's <c>version</c>.</param>
    /// <returns><see langword="true"/> when the node stops the run.</returns>
    internal static bool StopsRunningJobs(string? version)
    {
        string core = (version ?? string.Empty).Split('+')[0].Split('-')[0];
        return Version.TryParse(core, out Version? parsed) && parsed.Build >= 0 && parsed >= StopsRunsFrom;
    }

    /// <summary>Withdraws <paramref name="id"/>; never throws for the bus's sake.</summary>
    /// <param name="id">The job id.</param>
    /// <param name="why">The <c>--why</c> line (checked by the caller), or <see langword="null"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    internal async Task<WithdrawOutcome> WithdrawAsync(EnvelopeId id, string? why, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(id);
        BusSyncResult pull = await _bus.PullAsync(ct).ConfigureAwait(false);
        if (!pull.Succeeded)
        {
            return pull.Detail.StartsWith(BusRepository.BusyDetail, StringComparison.Ordinal)
                ? new WithdrawOutcome(WithdrawResult.Busy, Detail: pull.Detail)
                : new WithdrawOutcome(WithdrawResult.Failed, Detail: "pull failed: " + pull.Detail);
        }

        try
        {
            WithdrawOutcome first = await TryOnceAsync(id, why, ct).ConfigureAwait(false);
            if (first.Detail != LostRace)
            {
                return first;
            }

            // Spec 61 §1: the queued removal lost the race with the node's claim; the write pulled, so the state is read once more.
            WithdrawOutcome again = await TryOnceAsync(id, why, ct).ConfigureAwait(false);
            return again.Detail == LostRace ? again with { Detail = "the job moved while it was withdrawn; try again" } : again;
        }
        catch (BusBusyException ex)
        {
            return new WithdrawOutcome(WithdrawResult.Busy, Detail: ex.Message);
        }
    }

    private const string LostRace = "lost race";

    private async Task<WithdrawOutcome> TryOnceAsync(EnvelopeId id, string? why, CancellationToken ct)
    {
        IReadOnlyList<(string Path, MachineName Machine, bool Claimed, byte[] Bytes)> files = await _bus.FindJobFilesAsync(id, ct).ConfigureAwait(false);
        if (files.Count == 0)
        {
            EnvelopeTrace? trace = await _inventory.TraceAsync(id, ct).ConfigureAwait(false);
            return trace is { State: TraceState.Reported or TraceState.Rejected }
                ? new WithdrawOutcome(WithdrawResult.AlreadyReported, trace.Machine, EnvelopeWire.ToWire(trace.Reports.LastOrDefault(r => r.Verified)?.Status ?? ReportStatus.Rejected))
                : new WithdrawOutcome(WithdrawResult.NotFound);
        }

        (string path, MachineName target, bool claimed, byte[] bytes) = files[0];
        EnvelopeResult verified = await _signer.ParseAndVerifyAsync(bytes, _bus.Me.Tenant, ct).ConfigureAwait(false);
        if (!verified.IsAccepted || verified.Envelope!.Type != EnvelopeType.Job || verified.Envelope.Job is null)
        {
            return new WithdrawOutcome(WithdrawResult.DoesNotVerify, target, verified.Rejection is { } rejection ? EnvelopeWire.ToWire(rejection.Reason) : "not a job");
        }

        Model job = verified.Envelope;
        return claimed
            ? await RequestAsync(job, path, target, why, ct).ConfigureAwait(false)
            : await RemoveQueuedAsync(job, path, bytes, target, why, ct).ConfigureAwait(false);
    }

    private async Task<WithdrawOutcome> RemoveQueuedAsync(Model job, string path, byte[] bytes, MachineName target, string? why, CancellationToken ct)
    {
        WithdrawalVerdict verdict = WithdrawalRules.MayRemoveQueued(_bus.Me, job, path);
        if (verdict != WithdrawalVerdict.Allowed)
        {
            return Refusal(verdict, job, target);
        }

        DateTimeOffset now = _clock.GetUtcNow();
        var header = new EnvelopeHeader(_bus.Me.Tenant, EnvelopeId.New(now), _bus.Me.Machine, job.Header.From, now, EnvelopePriority.Normal, job.Header.Id);
        JobFields fields = job.Job!;
        var report = new ReportFields(ReportStatus.Failed, RunFailureReasonWire.ToWire(RunFailureReason.Withdrawn), Mandate: fields.Mandate, Step: fields.Step, JobType: fields.JobType);
        string body = "withdrawn before it started\n" + (why is null ? string.Empty : why + "\n");
        EnvelopeResult signed = await _signer.SignAsync(Model.CreateReport(header, report, Encoding.UTF8.GetBytes(body)), _signWith, ct).ConfigureAwait(false);
        if (!signed.IsAccepted)
        {
            return new WithdrawOutcome(WithdrawResult.Failed, target, "sign: " + signed.Rejection!.Detail);
        }

        WriteOutcome outcome = await _bus.WithdrawQueuedAsync(path, job, bytes, signed.Envelope!, ct).ConfigureAwait(false);
        switch (outcome)
        {
            case WriteFailed { Stage: "rm" or "rebase" }:
                return new WithdrawOutcome(WithdrawResult.Failed, target, LostRace);
            case WriteFailed failed:
                return new WithdrawOutcome(WithdrawResult.Failed, target, failed.Stage + ": " + failed.Detail);
            default:
                LogWithdrawn(job.Header.Id.Value, target.Value);
                return new WithdrawOutcome(WithdrawResult.Withdrawn, target, PushDeferred: outcome is Deferred);
        }
    }

    private async Task<WithdrawOutcome> RequestAsync(Model job, string path, MachineName target, string? why, CancellationToken ct)
    {
        DateTimeOffset now = _clock.GetUtcNow();
        var header = new EnvelopeHeader(_bus.Me.Tenant, EnvelopeId.New(now), _bus.Me.Machine, target, now, EnvelopePriority.Normal, job.Header.Id);
        EnvelopeResult signed = await _signer.SignAsync(Model.CreateWithdrawal(header, why), _signWith, ct).ConfigureAwait(false);
        if (!signed.IsAccepted)
        {
            return new WithdrawOutcome(WithdrawResult.Failed, target, "sign: " + signed.Rejection!.Detail);
        }

        // Nothing is written outside the rule: a refused withdrawal is signed in memory only and dropped.
        string withdrawalPath = BusPaths.EnvelopeFile(BusPaths.Withdrawals(_bus.Me.Tenant, target), header.Id);
        WithdrawalVerdict verdict = WithdrawalRules.MayWriteWithdrawal(_bus.Me, job, path, signed.Envelope!, withdrawalPath);
        if (verdict != WithdrawalVerdict.Allowed)
        {
            return Refusal(verdict, job, target);
        }

        WriteOutcome outcome = await _bus.RequestWithdrawalAsync(path, job, signed.Envelope!, ct).ConfigureAwait(false);
        if (outcome is WriteFailed failed)
        {
            return new WithdrawOutcome(WithdrawResult.Failed, target, failed.Stage + ": " + failed.Detail);
        }

        LogRequested(job.Header.Id.Value, target.Value);
        string? version = (await _inventory.RegistryAsync(ct).ConfigureAwait(false)).FirstOrDefault(d => d.Machine == target)?.Version;
        return new WithdrawOutcome(WithdrawResult.Requested, target, PushDeferred: outcome is Deferred, TargetVersion: StopsRunningJobs(version) ? null : version ?? "unknown");
    }

    private static WithdrawOutcome Refusal(WithdrawalVerdict verdict, Model job, MachineName target) => verdict switch
    {
        WithdrawalVerdict.SessionTask => new WithdrawOutcome(WithdrawResult.SessionTask, target),
        WithdrawalVerdict.NotSender => new WithdrawOutcome(WithdrawResult.NotSender, target, job.Header.From.Value),
        _ => new WithdrawOutcome(WithdrawResult.DoesNotVerify, target, verdict.ToString()),
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "job {Id} withdrawn before it started (was queued on {Target})")]
    private partial void LogWithdrawn(string id, string target);

    [LoggerMessage(Level = LogLevel.Information, Message = "withdrawal of job {Id} requested from {Target}")]
    private partial void LogRequested(string id, string target);
}
