using System.Globalization;
using System.Text;

using Microsoft.Extensions.Logging;

using Zyggy.Core.Envelope;
using Zyggy.Core.Runs;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Bus;

/// <content>Spec 61 slice 5: the node acts on the withdrawals in its own <c>withdrawals/</c> folder (Behavior §1, "On the node").</content>
public sealed partial class BusListener
{
    /// <summary>
    /// After every pull and at start-up, also while a run is alive: each withdrawal in this node's <c>withdrawals/</c> is verified and held to
    /// the rule (<see cref="WithdrawalRules.Accept"/>). A live run of the job is signalled to stop (its report carries the withdrawal's lines and
    /// removes it); a claimed or queued job without a run is answered at once with a <c>failed</c> / <c>withdrawn</c> report that removes the
    /// job's file and the withdrawal in one commit; a withdrawal whose job this node no longer holds is removed as "nothing to stop". One that
    /// does not verify, or is refused by the rule, is left in place with one Warning per file per process. A job whose result is kept (or
    /// waits to be signed) is left for that report: the withdrawal then has nothing to stop.
    /// </summary>
    private async Task HandleWithdrawalsAsync(CancellationToken ct)
    {
        foreach (BusFile file in await _bus.ListWithdrawalsAsync(ct).ConfigureAwait(false))
        {
            EnvelopeResult verified = await _bus.VerifyWithdrawalAsync(file, ct).ConfigureAwait(false);
            if (!verified.IsAccepted)
            {
                WarnWithdrawal(file, EnvelopeWire.ToWire(verified.Rejection!.Reason));
                continue;
            }

            Model withdrawal = verified.Envelope!;
            EnvelopeId? jobId = withdrawal.Header.InReplyTo;
            if (jobId is null || _withdrawing.ContainsKey(jobId.Value) || HasResult(jobId))
            {
                continue;
            }

            AcceptedJob? job = await _bus.FindOwnJobAsync(jobId, ct).ConfigureAwait(false);
            WithdrawalVerdict verdict = WithdrawalRules.Accept(_bus.Me, withdrawal, file.RelativePath, job?.Job);
            switch (verdict)
            {
                case WithdrawalVerdict.UnknownJob when !_runs.ContainsKey(jobId.Value):
                    await NothingToStopAsync(file, jobId, withdrawal.Header.From, ct).ConfigureAwait(false);
                    break;
                case WithdrawalVerdict.Allowed when _runs.ContainsKey(jobId.Value):
                    StopRun(file, withdrawal, jobId);
                    break;
                case WithdrawalVerdict.Allowed:
                    await WithdrawWithoutRunAsync(file, withdrawal, job!, ct).ConfigureAwait(false);
                    break;
                case WithdrawalVerdict.UnknownJob:
                    // The run is alive but its job's file is gone (Central answered it meanwhile): the run's own report answers it.
                    break;
                default:
                    WarnWithdrawal(file, verdict.ToString());
                    break;
            }
        }
    }

    /// <summary>The first lines of a withdrawn job's report: who withdrew it and when, then the <c>--why</c> line (data).</summary>
    private static string WithdrawnLines(Model withdrawal)
    {
        string when = withdrawal.Header.Created.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        string why = withdrawal.BodyText.TrimEnd('\n');
        return "withdrawn by " + withdrawal.Header.From.Value + " at " + when + "\n" + (why.Length == 0 ? string.Empty : why + "\n");
    }

    // A live run: the runner stops the model and finishes its worktree; the run's report removes the withdrawal (AnswerAsync).
    private void StopRun(BusFile file, Model withdrawal, EnvelopeId job)
    {
        if (Withdrawals is null)
        {
            // Without the job runner's signal (a probe handler) the run finishes and reports; the withdrawal then has nothing to stop.
            return;
        }

        _withdrawing[job.Value] = new SeenWithdrawal(file.RelativePath, withdrawal);
        Withdrawals.Withdraw(job);
        using IDisposable? scope = Scope("withdraw");
        LogStopping(job.Value, withdrawal.Header.From.Value);
    }

    // A claimed job the gate deferred or that waits to be resumed, or a queued copy after a re-queue: answered now, no run.
    private async Task WithdrawWithoutRunAsync(BusFile file, Model withdrawal, AcceptedJob job, CancellationToken ct)
    {
        lock (_deferredClaims)
        {
            _deferredClaims.RemoveAll(c => c.File.Id == job.File.Id);
        }

        var fields = new ReportFields(ReportStatus.Failed, RunFailureReasonWire.ToWire(RunFailureReason.Withdrawn), Mandate: job.Job.Job?.Mandate, Step: job.Job.Job?.Step, JobType: job.Job.Job?.JobType);
        using IDisposable? scope = Scope("withdraw");
        (Model? signed, WriteFailed? signFailed) = await _bus.SignReportAsync(job, fields, Encoding.UTF8.GetBytes(WithdrawnLines(withdrawal)), _signWith, ct).ConfigureAwait(false);
        if (signed is null)
        {
            LogWriteFailed("withdrawal", job.File.Id.Value, signFailed!.Stage, signFailed.Detail);
            return;
        }

        WriteOutcome outcome = await _bus.WriteReportAsync(signed, EnvelopeWriter.Serialize(signed), null, JobFingerprint.Of(job.Job), ct, file.RelativePath, LiveRuns(minus: job.File.Id)).ConfigureAwait(false);
        if (outcome is WriteFailed failed)
        {
            LogWriteFailed("withdrawal", job.File.Id.Value, failed.Stage, failed.Detail);
            return;
        }

        _lastReportUtc = _clock.GetUtcNow();
        LogWithdrawnWithoutRun(job.File.Id.Value, withdrawal.Header.From.Value, outcome is Deferred ? "deferred" : "pushed");
    }

    private async Task NothingToStopAsync(BusFile file, EnvelopeId job, Tenancy.MachineName from, CancellationToken ct)
    {
        using IDisposable? scope = Scope("withdraw");
        WriteOutcome outcome = await _bus.RemoveWithdrawalAsync(file, job, from, ct).ConfigureAwait(false);
        if (outcome is WriteFailed failed)
        {
            LogWriteFailed("withdrawal removal", job.Value, failed.Stage, failed.Detail);
            return;
        }

        LogNothingToStop(job.Value, from.Value);
    }

    private void WarnWithdrawal(BusFile file, string why)
    {
        if (!_warnedWithdrawals.Add(file.RelativePath))
        {
            return;
        }

        using IDisposable? scope = Scope("withdraw");
        LogWithdrawalRefused(Path.GetFileName(file.RelativePath), why);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "job {Id} withdrawn by {From}: stopping its run")]
    private partial void LogStopping(string id, string from);

    [LoggerMessage(Level = LogLevel.Information, Message = "job {Id} withdrawn by {From}: reported withdrawn without a run; {Push}")]
    private partial void LogWithdrawnWithoutRun(string id, string from, string push);

    [LoggerMessage(Level = LogLevel.Information, Message = "withdrawal of job {Id} by {From}: nothing to stop; removed")]
    private partial void LogNothingToStop(string id, string from);

    [LoggerMessage(Level = LogLevel.Warning, Message = "withdrawal {File} not accepted ({Why}); left in place, zyggy verify names it")]
    private partial void LogWithdrawalRefused(string file, string why);
}
