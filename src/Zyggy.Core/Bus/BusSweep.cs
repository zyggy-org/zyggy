using System.Globalization;
using System.Text;

using Microsoft.Extensions.Logging;

using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Bus;

/// <summary>
/// Central's sweep (spec 17; founding spec §4 state machine): every claim in a machine's <c>jobs/claimed/</c> that is older than
/// its <c>timeout_minutes</c> + 15 min is re-queued with <c>attempt</c> raised and re-signed, ended with Central's
/// <c>attempts_exhausted</c> report once it used three attempts, or rejected when it does not verify — one commit per claim. Then
/// every <c>reports/</c> and <c>context/</c> file older than 14 days, except what the pickup still takes, moves to
/// <c>archive/YYYY-MM/</c> in one commit. Never calls a model, never throws to the caller except on cancellation. Built only by <see cref="ServiceCollectionExtensions.AddBusSweep"/>;
/// public so <c>zyggy sweep</c> can run a pass, like <see cref="BusPickup"/>.
/// </summary>
public sealed partial class BusSweep
{
    /// <summary>The report reason of a job whose third claim went stale (founding spec §4 as amended by deliverable 17); written only here.</summary>
    internal const string AttemptsExhaustedReason = "attempts_exhausted";

    /// <summary>The reason of Central's report on a session task no live session took (deliverable 18).</summary>
    internal const string NoSessionReason = "no_session";

    /// <summary>The attempts a job gets (§4); a stale claim at this attempt or above is exhausted.</summary>
    private const int MaxAttempts = 3;

    /// <summary>What a claim may exceed its timeout by before it is stale (§4).</summary>
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(15);

    /// <summary>The age past which a <c>reports/</c> or <c>context/</c> file is archived (§4).</summary>
    private static readonly TimeSpan ArchiveAge = TimeSpan.FromDays(14);

    private readonly BusRepository _bus;
    private readonly EnvelopeSigner _signer;
    private readonly SweepOptions _options;
    private readonly ILogger<BusSweep> _log;
    private readonly HashSet<string> _warnedUnreadable = new(StringComparer.Ordinal);
    private bool _failureLogged;
    private bool _signFailureLogged;

    internal BusSweep(BusRepository bus, EnvelopeSigner signer, SweepOptions options, ILogger<BusSweep> log)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Runs one pass over the stale claims of every machine of the tenant. The pass does not pull first: each write pulls, and the
    /// pull before a pass belongs to the caller (<c>zyggy sweep</c>, Central's node).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>What the pass did; a failure is logged once per streak and carried in the result.</returns>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="ct"/> is cancelled.</exception>
    public Task<SweepResult> RunAsync(CancellationToken ct) => RunAsync(dryRun: false, ct);

    /// <summary>
    /// Runs one pass; with <paramref name="dryRun"/> it decides every claim and archive move as a pass would, but signs nothing, writes
    /// nothing (no file, no commit, no push) and logs no action line — what <c>zyggy sweep --dry-run</c> prints (spec 17 CLI).
    /// </summary>
    /// <param name="dryRun">Whether to only report what a pass would do.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>What the pass did, or would do; a failure is logged once per streak and carried in the result.</returns>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="ct"/> is cancelled.</exception>
    public async Task<SweepResult> RunAsync(bool dryRun, CancellationToken ct)
    {
        using IDisposable? scope = _log.BeginScope(new Dictionary<string, object>
        {
            ["tenant"] = _bus.Me.Tenant.Value,
            ["machine"] = _bus.Me.Machine.Value,
            ["phase"] = "sweep",
        });
        var actions = new List<SweepAction>();
        try
        {
            return await RunCoreAsync(actions, dryRun, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // spec 17 AC-16: the sweep never throws to the caller; one Error names the type.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            var failed = new WriteFailed("pass", ex.GetType().Name);
            NoteFailed(failed);
            return new SweepResult(actions, failed, false);
        }
    }

    private static string Utc(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private async Task<SweepResult> RunCoreAsync(List<SweepAction> actions, bool dryRun, CancellationToken ct)
    {
        IReadOnlyList<SweepClaim> claims;
        try
        {
            claims = await _bus.ListClaimsForSweepAsync(ct).ConfigureAwait(false);
        }
        catch (BusBusyException ex)
        {
            var busy = new WriteFailed("lock", ex.Message);
            NoteFailed(busy);
            return new SweepResult(actions, busy, false);
        }

        DateTimeOffset now = _bus.Clock.GetUtcNow();
        WriteFailed? failure = null;
        bool deferred = false;
        bool? canSign = null;
        foreach (SweepClaim claim in claims)
        {
            if (claim.ClaimTime is not { } claimed)
            {
                if (_warnedUnreadable.Add(claim.File.RelativePath))
                {
                    LogClaimTimeUnreadable(claim.File.RelativePath);
                }

                continue;
            }

            EnvelopeResult parsed = EnvelopeParser.Parse(claim.Bytes, _bus.Me.Tenant);
            int timeout = (parsed.IsAccepted ? parsed.Envelope.Job?.TimeoutMinutes : null) ?? _options.DefaultTimeoutMinutes;
            if (now - claimed <= TimeSpan.FromMinutes(timeout) + Grace)
            {
                continue;
            }

            // Nothing unsigned is written: without a usable key no stale claim is moved, removed or reported.
            canSign ??= await CanSignAsync(ct).ConfigureAwait(false);
            if (canSign == false)
            {
                continue;
            }

            (WriteOutcome? outcome, SweepAction action) = await SettleAsync(claim, claimed, parsed, dryRun, ct).ConfigureAwait(false);
            if (outcome is WriteFailed failed)
            {
                failure ??= failed;
                NoteFailed(failed);
                continue;
            }

            deferred |= outcome is Deferred;
            actions.Add(action);
            if (dryRun)
            {
                continue;
            }

            switch (action.Kind)
            {
                case SweepActionKind.Requeued:
                    LogRequeued(action.Id.Value, action.Machine.Value, Utc(claimed), action.Attempt);
                    break;
                case SweepActionKind.Exhausted:
                    LogExhausted(action.Id.Value, action.Machine.Value, action.Attempt);
                    break;
                default:
                    LogRejected(action.Id.Value, action.Machine.Value, action.Reason ?? "-");
                    break;
            }
        }

        // Deliverable 18: a session task still unclaimed after its timeout is removed with Central's failed / no_session report.
        foreach ((WriteOutcome? outcome, SweepAction action) in await ExpireSessionTasksAsync(now, dryRun, async () => canSign ??= await CanSignAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false))
        {
            if (outcome is WriteFailed failed)
            {
                failure ??= failed;
                NoteFailed(failed);
                continue;
            }

            deferred |= outcome is Deferred;
            actions.Add(action);
            if (!dryRun)
            {
                LogSessionExpired(action.Id.Value, action.Machine.Value);
            }
        }

        (WriteOutcome? archived, int count, DateTimeOffset? oldest) = await ArchiveAsync(dryRun, ct).ConfigureAwait(false);
        if (archived is WriteFailed archiveFailed)
        {
            failure ??= archiveFailed;
            NoteFailed(archiveFailed);
            count = 0;
            oldest = null;
        }
        else if (archived is not null)
        {
            deferred |= archived is Deferred;
            string oldestText = Utc(oldest!.Value);
            LogArchived(count, oldestText, archived is Deferred ? "deferred" : "pushed");
        }

        if (failure is null)
        {
            _failureLogged = false;
        }

        return new SweepResult(actions, failure, deferred) { Archived = count, OldestArchived = oldest };
    }

    // Step 2 of the pass: every reports/ and context/ file older than 14 days, except what the pickup still takes, in one commit.
    // The outcome is null when nothing moved: nothing to archive, or a dry run (which still counts what it would move).
    private async Task<(WriteOutcome? Outcome, int Count, DateTimeOffset? Oldest)> ArchiveAsync(bool dryRun, CancellationToken ct)
    {
        IReadOnlyList<SweepArchiveFile> files;
        try
        {
            files = await _bus.ListArchiveForSweepAsync(ct).ConfigureAwait(false);
        }
        catch (BusBusyException ex)
        {
            return (new WriteFailed("lock", ex.Message), 0, null);
        }

        DateTimeOffset now = _bus.Clock.GetUtcNow();
        var moves = new List<(BusFile File, DateTimeOffset Created)>();
        foreach (SweepArchiveFile file in files)
        {
            if (file.Dated is not { } dated || now - dated <= ArchiveAge || await PickupTakesAsync(file, ct).ConfigureAwait(false))
            {
                continue;
            }

            moves.Add((file.File, dated));
        }

        if (moves.Count == 0)
        {
            return (null, 0, null);
        }

        DateTimeOffset oldest = moves.Min(m => m.Created);
        return dryRun
            ? (null, moves.Count, oldest)
            : (await _bus.SettleAsync(moves, [], ct).ConfigureAwait(false), moves.Count, oldest);
    }

    // A file the pickup still takes stays where it is, whatever its age (spec 17 "The pass", step 2).
    private async Task<bool> PickupTakesAsync(SweepArchiveFile file, CancellationToken ct)
    {
        if (file.Folder == MachineName.Central)
        {
            return false;
        }

        EnvelopeResult verified = await _signer.ParseAndVerifyAsync(file.Bytes, _bus.Me.Tenant, ct).ConfigureAwait(false);
        return verified.IsAccepted && BusPickup.Takes(new PickupCandidate(file.File, file.Folder, file.Kind, file.Bytes), verified.Envelope!);
    }

    // One stale claim: rejected when it does not verify, exhausted at three attempts or more, otherwise re-queued. A dry run returns
    // the decision with no outcome, before anything is signed or written.
    private async Task<(WriteOutcome? Outcome, SweepAction Action)> SettleAsync(SweepClaim claim, DateTimeOffset claimed, EnvelopeResult parsed, bool dryRun, CancellationToken ct)
    {
        EnvelopeId id = claim.File.Id;
        if (await RefusalAsync(claim, parsed, ct).ConfigureAwait(false) is { } refusal)
        {
            string reason = EnvelopeWire.ToWire(refusal.Reason);
            var rejected = new SweepAction(SweepActionKind.Rejected, id, claim.Folder, parsed.IsAccepted ? parsed.Envelope.Job?.Attempt ?? 1 : 1, reason);
            if (dryRun)
            {
                return (null, rejected);
            }

            if (EnvelopeParser.PeekFrom(claim.Bytes) is not { } from)
            {
                return (await _bus.RejectStaleAsync(claim, null, ct).ConfigureAwait(false), rejected);
            }

            string body = "rejected: " + reason + (refusal.Field is null ? string.Empty : " (" + refusal.Field + ")") + "\n";
            JobFields? refused = parsed.IsAccepted ? parsed.Envelope.Job : null;
            EnvelopeResult rejection = await SignReportAsync(from, id, new ReportFields(ReportStatus.Rejected, reason, Mandate: refused?.Mandate, Step: refused?.Step), body, ct).ConfigureAwait(false);
            return rejection.IsAccepted
                ? (await _bus.RejectStaleAsync(claim, rejection.Envelope, ct).ConfigureAwait(false), rejected)
                : (new WriteFailed("sign", rejection.Rejection.Detail), rejected);
        }

        Model job = parsed.Envelope!;
        int attempt = job.Job!.Attempt;
        if (dryRun)
        {
            return attempt >= MaxAttempts
                ? (null, new SweepAction(SweepActionKind.Exhausted, id, claim.Folder, attempt, null))
                : (null, new SweepAction(SweepActionKind.Requeued, id, claim.Folder, attempt + 1, null));
        }

        if (attempt >= MaxAttempts)
        {
            string body = string.Create(CultureInfo.InvariantCulture, $"machine: {claim.Folder.Value}\nattempts: {attempt}\nclaimed: {Utc(claimed)}\n");
            EnvelopeResult report = await SignReportAsync(job.Header.From, id, new ReportFields(ReportStatus.Failed, AttemptsExhaustedReason, Mandate: job.Job.Mandate, Step: job.Job.Step), body, ct).ConfigureAwait(false);
            var exhausted = new SweepAction(SweepActionKind.Exhausted, id, claim.Folder, attempt, null);
            return report.IsAccepted
                ? (await _bus.ExhaustAsync(claim, report.Envelope, ct).ConfigureAwait(false), exhausted)
                : (new WriteFailed("sign", report.Rejection.Detail), exhausted);
        }

        // The only field that changes is attempt; signing sets key_id and sig. The canonical form is the signer's own.
        EnvelopeResult signed = await _signer.SignAsync(job.WithAttempt(attempt + 1), _options.SignWith!, ct).ConfigureAwait(false);
        var requeued = new SweepAction(SweepActionKind.Requeued, id, claim.Folder, attempt + 1, null);
        return signed.IsAccepted
            ? (await _bus.RequeueAsync(claim, signed.Envelope, ct).ConfigureAwait(false), requeued)
            : (new WriteFailed("sign", signed.Rejection.Detail), requeued);
    }

    /// <summary>
    /// Why a claim does not verify (the audit's checks, spec 17 Context), or <see langword="null"/> when it does: parses as a job,
    /// signature valid for the tenant, file name = <c>id</c>, <c>to</c> = the folder's machine.
    /// </summary>
    private async Task<(EnvelopeRejectionReason Reason, string? Field)?> RefusalAsync(SweepClaim claim, EnvelopeResult parsed, CancellationToken ct)
    {
        if (!parsed.IsAccepted)
        {
            return (parsed.Rejection.Reason, parsed.Rejection.Field);
        }

        EnvelopeResult verified = await _signer.VerifyAsync(parsed.Envelope, _bus.Me.Tenant, ct).ConfigureAwait(false);
        if (!verified.IsAccepted)
        {
            return (verified.Rejection.Reason, verified.Rejection.Field);
        }

        Model envelope = parsed.Envelope;
        if (envelope.Type != EnvelopeType.Job)
        {
            return (EnvelopeRejectionReason.Malformed, "type");
        }

        if (!string.Equals(Path.GetFileNameWithoutExtension(claim.File.RelativePath), envelope.Header.Id.Value, StringComparison.Ordinal))
        {
            return (EnvelopeRejectionReason.Malformed, "id");
        }

        return envelope.Header.To != claim.Folder ? (EnvelopeRejectionReason.WrongRecipient, "to") : null;
    }

    // Central's signed report to a job's sender, in reply to the claimed file.
    private Task<EnvelopeResult> SignReportAsync(MachineName to, EnvelopeId inReplyTo, ReportFields fields, string body, CancellationToken ct)
    {
        DateTimeOffset now = _bus.Clock.GetUtcNow();
        var header = new EnvelopeHeader(_bus.Me.Tenant, EnvelopeId.New(now), _bus.Me.Machine, to, now, EnvelopePriority.Normal, inReplyTo);
        return _signer.SignAsync(Model.CreateReport(header, fields, Encoding.UTF8.GetBytes(body)), _options.SignWith!, ct);
    }

    // Every verified deliver: session job in a machine's jobs/ whose created plus timeout_minutes (60 by default) has passed.
    private async Task<List<(WriteOutcome? Outcome, SweepAction Action)>> ExpireSessionTasksAsync(DateTimeOffset now, bool dryRun, Func<Task<bool?>> canSign, CancellationToken ct)
    {
        var done = new List<(WriteOutcome? Outcome, SweepAction Action)>();
        IReadOnlyList<SweepClaim> tasks;
        try
        {
            tasks = await _bus.ListSessionTasksForSweepAsync(ct).ConfigureAwait(false);
        }
        catch (BusBusyException ex)
        {
            done.Add((new WriteFailed("lock", ex.Message), new SweepAction(SweepActionKind.SessionExpired, EnvelopeId.New(now), _bus.Me.Machine, 1, NoSessionReason)));
            return done;
        }

        foreach (SweepClaim task in tasks)
        {
            if (EnvelopeParser.PeekHeader(task.Bytes) is not { Type: EnvelopeType.Job } || task.ClaimTime is not { } created)
            {
                continue;
            }

            EnvelopeResult verified = await _signer.ParseAndVerifyAsync(task.Bytes, _bus.Me.Tenant, ct).ConfigureAwait(false);
            if (!verified.IsAccepted
                || verified.Envelope.Job is not { Deliver: JobDelivery.Session } job
                || verified.Envelope.Header.Id != task.File.Id
                || verified.Envelope.Header.To != task.Folder
                || now <= created + TimeSpan.FromMinutes(job.TimeoutMinutes ?? BusRepository.SessionTaskTimeoutMinutes))
            {
                continue;
            }

            var action = new SweepAction(SweepActionKind.SessionExpired, task.File.Id, task.Folder, job.Attempt, NoSessionReason);
            if (dryRun)
            {
                done.Add((null, action));
                continue;
            }

            if (await canSign().ConfigureAwait(false) == false)
            {
                break;
            }

            string body = string.Create(CultureInfo.InvariantCulture, $"machine: {task.Folder.Value}\nsubmitted: {Utc(created)}\ntimeout_minutes: {job.TimeoutMinutes ?? BusRepository.SessionTaskTimeoutMinutes}\n");
            EnvelopeResult report = await SignReportAsync(verified.Envelope.Header.From, task.File.Id, new ReportFields(ReportStatus.Failed, NoSessionReason, Mandate: job.Mandate, Step: job.Step), body, ct).ConfigureAwait(false);
            done.Add(report.IsAccepted
                ? (await _bus.ExpireSessionTaskAsync(task, report.Envelope, ct).ConfigureAwait(false), action)
                : (new WriteFailed("sign", report.Rejection.Detail), action));
        }

        return done;
    }

    private async Task<bool> CanSignAsync(CancellationToken ct)
    {
        string? detail = "signWith missing: Central must sign what the sweep writes";
        if (_options.SignWith is { } signWith)
        {
            SigningKeyCheck key = await _signer.CheckKeyAsync(signWith, ct).ConfigureAwait(false);
            detail = key.State == SigningKeyState.Usable ? null : key.Detail;
        }

        if (detail is null)
        {
            _signFailureLogged = false;
            return true;
        }

        if (!_signFailureLogged)
        {
            _signFailureLogged = true;
            LogCannotSign(detail);
        }

        return false;
    }

    private void NoteFailed(WriteFailed failed)
    {
        if (!_failureLogged)
        {
            _failureLogged = true;
            LogFailed(failed.Stage, failed.Detail);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "job {Id} on {Machine} stuck since {ClaimTime}: re-queued, attempt {Attempt}")]
    private partial void LogRequeued(string id, string machine, string claimTime, int attempt);

    [LoggerMessage(Level = LogLevel.Error, Message = "job {Id} on {Machine} failed after {Attempts} attempts")]
    private partial void LogExhausted(string id, string machine, int attempts);

    [LoggerMessage(Level = LogLevel.Warning, Message = "session task {Id} on {Machine} not taken within its timeout: removed, no_session")]
    private partial void LogSessionExpired(string id, string machine);

    [LoggerMessage(Level = LogLevel.Warning, Message = "claimed job {Id} on {Machine} does not verify ({Reason}): rejected")]
    private partial void LogRejected(string id, string machine, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "archived {Count} file(s) older than 14 days, oldest created {Oldest}; {State}")]
    private partial void LogArchived(int count, string oldest, string state);

    [LoggerMessage(Level = LogLevel.Warning, Message = "sweep: claim time of {Path} unreadable; claim left")]
    private partial void LogClaimTimeUnreadable(string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "sweep: cannot sign: {Detail}")]
    private partial void LogCannotSign(string detail);

    [LoggerMessage(Level = LogLevel.Error, Message = "sweep: failed at {Stage}: {Detail}")]
    private partial void LogFailed(string stage, string detail);
}
