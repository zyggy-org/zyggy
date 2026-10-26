using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

using Zyggy.Core.Envelope;
using Zyggy.Core.Jobs;

namespace Zyggy.Core.Bus;

/// <summary>
/// The receiver's loop (founding spec §5 poll loop), hosted by the probe verb now and by the Node service later: start-up
/// check, one pull, then per tick — wait until the poller says so, ring the doorbell, pull only when the branch moved, and
/// for every inbox file verify → claim → handler → report, or reject. One request in flight at a time; one log line per
/// state change and nothing on a silent tick (§11); every entry scoped with <c>tenant</c>, <c>machine</c> and <c>phase</c>.
/// With a <see cref="JobGate"/> (the Node, spec 07) a verified job is admitted or deferred <em>before</em> the claim, and an admitted
/// job's run and report continue as a tracked task so the loop keeps polling; without one (the probe verbs) every job is admitted
/// and awaited inline, exactly as in deliverable 04.
/// </summary>
public sealed partial class BusListener : IDisposable
{
    private readonly IBusProvider _provider;
    private readonly BusRepository _bus;
    private readonly PollOptions _options;
    private readonly KeyId _signWith;
    private readonly TimeProvider _clock;
    private readonly Random _jitter;
    private readonly JobHandler _handler;
    private readonly ILogger<BusListener> _log;
    private readonly JobGate? _gate;
    private readonly ConcurrentDictionary<string, Task> _runs = new(StringComparer.Ordinal);
    private readonly List<AcceptedJob> _deferredClaims = [];
    private bool _pullErrorLogged;
    private IDisposable? _listenLock;

    /// <summary>The sentence behind a <see cref="BusStartupResult.KeyMissing"/> or <see cref="BusStartupResult.StoreUnavailable"/> start-up (spec 09 AC-6); null otherwise.</summary>
    public string? StartupDetail { get; private set; }
    private DateTimeOffset? _lastPollUtc;
    private DateTimeOffset? _lastPullUtc;
    private DateTimeOffset? _lastReportUtc;
    private string? _busHeadSha;
    private int _jobsClaimedByMe;
    private PollMode _mode = PollMode.Active;
    private DateTimeOffset? _nextSafetyFetchUtc;
    private bool _safetyFetchFailedLogged;

    /// <summary>Creates the listener.</summary>
    /// <param name="provider">The bus host seam.</param>
    /// <param name="bus">The repository layer for this node.</param>
    /// <param name="options">The poller's intervals.</param>
    /// <param name="signWith">The key this node signs reports with.</param>
    /// <param name="clock">The clock; the only time source.</param>
    /// <param name="jitter">The random source for the poll jitter.</param>
    /// <param name="handler">What to do with a verified, claimed job.</param>
    /// <param name="log">The logger.</param>
    /// <param name="gate">Consulted before every claim (spec 07); <see langword="null"/> admits every job and awaits its run inline.</param>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    public BusListener(
        IBusProvider provider,
        BusRepository bus,
        PollOptions options,
        KeyId signWith,
        TimeProvider clock,
        Random jitter,
        JobHandler handler,
        ILogger<BusListener> log,
        JobGate? gate = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _signWith = signWith ?? throw new ArgumentNullException(nameof(signWith));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _jitter = jitter ?? throw new ArgumentNullException(nameof(jitter));
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _gate = gate;
        State = PollState.Initial(clock.GetUtcNow());
        Status = new ListenerStatus(State, null, null, null, null, 0, 0, PollMode.Active);
    }

    /// <summary>Gets the poller's state: health for the Node service and the probe's status line.</summary>
    public PollState State { get; private set; }

    /// <summary>Gets the health snapshot (spec 07): replaced as a whole on every change, safe to read from another thread.</summary>
    public ListenerStatus Status { get; private set; }

    /// <summary>
    /// The §5 start-up rule, then one pull and one pass over what is already waiting. A dirty tree is stashed; a wrong branch,
    /// a missing repository or tenant directory, a rebase in progress or a missing signing key is returned without pulling.
    /// </summary>
    /// <remarks>The owner-facing sentence for <see cref="BusStartupResult.KeyMissing"/> and <see cref="BusStartupResult.StoreUnavailable"/> is in <see cref="StartupDetail"/>.</remarks>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>What the start-up check found.</returns>
    public async Task<BusStartupResult> StartAsync(CancellationToken ct)
    {
        BusStartupResult result = await _bus.EnsureCleanAsync(ct).ConfigureAwait(false);
        if (result is not (BusStartupResult.Clean or BusStartupResult.Stashed))
        {
            return result;
        }

        SigningKeyCheck key = await _bus.CheckSigningKeyAsync(_signWith, ct).ConfigureAwait(false);
        if (key.State != SigningKeyState.Usable)
        {
            StartupDetail = key.Detail;
            return key.State == SigningKeyState.StoreUnavailable ? BusStartupResult.StoreUnavailable : BusStartupResult.KeyMissing;
        }

        _listenLock ??= await _bus.TryAcquireListenLockAsync(ct).ConfigureAwait(false);
        if (_listenLock is null)
        {
            return BusStartupResult.ListenerRunning;
        }

        BusSyncResult pull = await _bus.PullAsync(ct).ConfigureAwait(false);
        if (!pull.Succeeded)
        {
            using IDisposable? scope = Scope("pull");
            LogPullFailed(pull.Detail);
        }
        else
        {
            await NotePulledAsync(ct).ConfigureAwait(false);
        }

        await ProcessOnceAsync(ct).ConfigureAwait(false);
        State = PollState.Initial(_clock.GetUtcNow());
        _nextSafetyFetchUtc = State.NextDueUtc + _options.SafetyFetchInterval;
        Publish();
        return result;
    }

    /// <summary>
    /// Resumes this node's own claims after the start-up pull (spec 07, A7-5): every <c>jobs/claimed/*.md</c> of this machine is
    /// re-verified; a verifying one goes through the gate and, when admitted, through the handler and the report with <em>no second
    /// claim commit</em>; a deferred one waits inside its claim and is re-examined at the start of every later pass; one that does
    /// not verify is left for the sweep with one warning. Silent when there is nothing to resume.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How many claims were resumed (admitted or deferred).</returns>
    public async Task<int> ResumeClaimedAsync(CancellationToken ct)
    {
        IReadOnlyList<BusFile> files = await _bus.ListClaimedAsync(ct).ConfigureAwait(false);
        HashSet<string> waiting;
        lock (_deferredClaims)
        {
            waiting = [.. _deferredClaims.Select(c => c.File.Id.Value)];
        }

        files = [.. files.Where(f => !_runs.ContainsKey(f.Id.Value) && !waiting.Contains(f.Id.Value))];
        if (files.Count == 0)
        {
            return 0;
        }

        using (Scope("claim"))
        {
            LogResuming(files.Count);
        }

        int resumed = 0;
        foreach (BusFile file in files)
        {
            JobIntake intake = await _bus.VerifyClaimedAsync(file, ct).ConfigureAwait(false);
            if (intake is not AcceptedJob accepted)
            {
                using (Scope("claim"))
                {
                    LogClaimedNotVerifying(file.Id.Value);
                }

                continue;
            }

            resumed++;
            if (!await AdmitAndTrackClaimedAsync(accepted, ct).ConfigureAwait(false))
            {
                lock (_deferredClaims)
                {
                    _deferredClaims.Add(accepted);
                }
            }
        }

        Publish();
        return resumed;
    }

    /// <summary>Waits until every tracked run has ended (the host calls it after cancelling, plan 07 A13); their faults were logged where they happened.</summary>
    /// <param name="ct">Cancellation token for the wait itself.</param>
    /// <returns>A task that completes when no run is pending.</returns>
    public async Task DrainAsync(CancellationToken ct)
    {
        Task[] pending = [.. _runs.Values];
        if (pending.Length == 0)
        {
            return;
        }

        await Task.WhenAll(pending).ContinueWith(_ => { }, ct, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Releases the one-listener-per-checkout lock (patch 0.8.1).</summary>
    public void Dispose()
    {
        _listenLock?.Dispose();
        _listenLock = null;
    }

    /// <summary>
    /// One pass over the inbox: every file is verified before anything else; an accepted job is claimed, handed to the
    /// handler only when the claim is pushed or deferred, and answered with a signed report; a refused file is rejected.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How many inbox files were handled (claimed or rejected).</returns>
    public async Task<int> ProcessOnceAsync(CancellationToken ct)
    {
        await RetryDeferredClaimsAsync(ct).ConfigureAwait(false);
        int handled = 0;
        int deferredBefore = await _bus.UnpushedCommitCountAsync(ct).ConfigureAwait(false);
        foreach (BusFile file in await _bus.ListNewJobsAsync(ct).ConfigureAwait(false))
        {
            JobIntake intake = await _bus.VerifyIntakeAsync(file, ct).ConfigureAwait(false);
            switch (intake)
            {
                case RejectedJob rejected:
                    using (Scope("reject"))
                    {
                        WriteOutcome outcome = await _bus.RejectAsync(rejected, _signWith, ct).ConfigureAwait(false);
                        deferredBefore = await NoteDeferredAsync(outcome, deferredBefore, ct).ConfigureAwait(false);
                        if (outcome is not WriteFailed)
                        {
                            handled++;
                        }
                    }

                    break;
                case AcceptedJob accepted:
                    if (await AdmitClaimAndTrackAsync(accepted, ct).ConfigureAwait(false))
                    {
                        handled++;
                    }

                    deferredBefore = await _bus.UnpushedCommitCountAsync(ct).ConfigureAwait(false);
                    break;
                default:
                    break;
            }
        }

        Publish();
        return handled;
    }

    /// <summary>
    /// The loop with a fixed mode (the probe verbs: always <see cref="PollMode.Active"/>); see <see cref="RunAsync(PollModeSelector, CancellationToken)"/>.
    /// </summary>
    /// <param name="mode">The host's poll mode.</param>
    /// <param name="ct">Cancellation token; cancellation ends the loop normally.</param>
    /// <returns>A task that completes when the loop ends.</returns>
    public Task RunAsync(PollMode mode, CancellationToken ct) => RunAsync(_ => mode, ct);

    /// <summary>
    /// The loop (§5): wait until the poller's next due instant or the next safety fetch, whichever is first; a due safety fetch
    /// compares <c>origin/&lt;branch&gt;</c> with <c>HEAD</c> and, when the branch moved behind the doorbell, warns, forgets the ETag,
    /// pulls and processes; a due tick re-evaluates the mode (one <c>poll mode</c> line per change; <c>Idle → Active</c> processes the
    /// inbox without a pull), rings the doorbell, applies the outcome, and pulls and processes when the branch moved — once per tick.
    /// Ends when <paramref name="ct"/> is cancelled.
    /// </summary>
    /// <param name="mode">The host's poll mode per instant (the Node: its work hours).</param>
    /// <param name="ct">Cancellation token; cancellation ends the loop normally.</param>
    /// <returns>A task that completes when the loop ends.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="mode"/> is null.</exception>
    public async Task RunAsync(PollModeSelector mode, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mode);
        _nextSafetyFetchUtc ??= _clock.GetUtcNow() + _options.SafetyFetchInterval;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                DateTimeOffset due = State.NextDueUtc < _nextSafetyFetchUtc.Value ? State.NextDueUtc : _nextSafetyFetchUtc.Value;
                TimeSpan wait = due - _clock.GetUtcNow();
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, _clock, ct).ConfigureAwait(false);
                }

                DateTimeOffset now = _clock.GetUtcNow();
                bool pulledThisTick = false;
                if (now >= _nextSafetyFetchUtc.Value)
                {
                    pulledThisTick = await SafetyFetchAsync(ct).ConfigureAwait(false);
                    _nextSafetyFetchUtc = now + _options.SafetyFetchInterval;
                }

                if (now < State.NextDueUtc)
                {
                    continue;
                }

                PollMode currentMode = mode(now);
                if (currentMode != _mode)
                {
                    bool opened = _mode == PollMode.Idle && currentMode == PollMode.Active;
                    _mode = currentMode;
                    using (Scope("poll"))
                    {
                        string modeText = currentMode.ToString();
                        LogPollMode(modeText);
                    }

                    if (opened && !pulledThisTick)
                    {
                        await ProcessOnceAsync(ct).ConfigureAwait(false);
                    }
                }

                HeadProbe outcome = await _provider.ProbeHeadAsync(State.ETag, ct).ConfigureAwait(false);
                _lastPollUtc = _clock.GetUtcNow();
                PollTransition transition = State.Apply(outcome, currentMode, _lastPollUtc.Value, (_jitter.NextDouble() * 2) - 1, _options);
                State = transition.State;
                Publish();
                if (transition.PhaseChanged is { } phase)
                {
                    using IDisposable? scope = Scope("poll");
                    string detail = outcome is Unreachable unreachable ? unreachable.Detail : string.Empty;
                    if (phase == PollPhase.Polling)
                    {
                        LogPhasePolling();
                    }
                    else
                    {
                        LogPhaseBackoff(phase, State.BackoffUntilUtc, detail);
                    }

                    _pullErrorLogged = false;
                }

                if (!transition.Pull || pulledThisTick)
                {
                    continue;
                }

                if (await PullAndNoteAsync(ct).ConfigureAwait(false))
                {
                    await ProcessOnceAsync(ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // the host asked us to stop
        }
    }

    /// <summary>The §5 item 6 safety fetch: Moved ⇒ one warning, the ETag forgotten, a pull and a pass; Failed ⇒ one warning per streak; Unchanged ⇒ silence. True when a pull happened.</summary>
    private async Task<bool> SafetyFetchAsync(CancellationToken ct)
    {
        SafetyFetchResult result = await _bus.SafetyFetchAsync(ct).ConfigureAwait(false);
        switch (result.Outcome)
        {
            case SafetyFetchOutcome.Moved:
                _safetyFetchFailedLogged = false;
                using (Scope("pull"))
                {
                    LogSafetyFetchMoved();
                }

                State = State with { ETag = null };
                if (await PullAndNoteAsync(ct).ConfigureAwait(false))
                {
                    await ProcessOnceAsync(ct).ConfigureAwait(false);
                    return true;
                }

                return false;
            case SafetyFetchOutcome.Failed:
                if (!_safetyFetchFailedLogged)
                {
                    _safetyFetchFailedLogged = true;
                    using (Scope("pull"))
                    {
                        LogSafetyFetchFailed(result.Detail);
                    }
                }

                return false;
            default:
                _safetyFetchFailedLogged = false;
                return false;
        }
    }

    /// <summary>One pull with the once-per-streak error line; true when it succeeded.</summary>
    private async Task<bool> PullAndNoteAsync(CancellationToken ct)
    {
        using (Scope("pull"))
        {
            BusSyncResult pull = await _bus.PullAsync(ct).ConfigureAwait(false);
            if (!pull.Succeeded)
            {
                if (!_pullErrorLogged)
                {
                    _pullErrorLogged = true;
                    LogPullFailed(pull.Detail);
                }

                return false;
            }

            _pullErrorLogged = false;
            await NotePulledAsync(ct).ConfigureAwait(false);
            return true;
        }
    }

    /// <summary>Gate → claim (inline) → run and report (tracked when there is a gate, awaited when there is none). False when nothing was claimed.</summary>
    private async Task<bool> AdmitClaimAndTrackAsync(AcceptedJob accepted, CancellationToken ct)
    {
        IDisposable lease;
        if (_gate is null)
        {
            lease = NoopLease.Instance;
        }
        else
        {
            AdmissionDecision decision = await _gate(accepted.Job, ct).ConfigureAwait(false);
            if (decision is not Admitted admitted)
            {
                return false;
            }

            lease = admitted.Lease;
        }

        int deferredBefore = await _bus.UnpushedCommitCountAsync(ct).ConfigureAwait(false);
        WriteOutcome claim;
        using (Scope("claim"))
        {
            claim = await _bus.ClaimAsync(accepted, ct).ConfigureAwait(false);
            if (claim is WriteFailed failed)
            {
                LogWriteFailed("claim", accepted.File.Id.Value, failed.Stage, failed.Detail);
                lease.Dispose();
                return false;
            }

            LogClaimed(accepted.File.Id.Value, accepted.Job.Header.From.Value, claim is Deferred ? "deferred" : "pushed");
            deferredBefore = await NoteDeferredAsync(claim, deferredBefore, ct).ConfigureAwait(false);
        }

        Task run = Track(accepted.File.Id.Value, RunAndReportAsync(accepted, lease, deferredBefore, ct));
        if (_gate is null)
        {
            await run.ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>A resumed claim: gate → handler → report, the claim step skipped. False when the gate deferred it.</summary>
    private async Task<bool> AdmitAndTrackClaimedAsync(AcceptedJob accepted, CancellationToken ct)
    {
        IDisposable lease;
        if (_gate is null)
        {
            lease = NoopLease.Instance;
        }
        else
        {
            AdmissionDecision decision = await _gate(accepted.Job, ct).ConfigureAwait(false);
            if (decision is not Admitted admitted)
            {
                return false;
            }

            lease = admitted.Lease;
        }

        int deferredBefore = await _bus.UnpushedCommitCountAsync(ct).ConfigureAwait(false);
        Task run = Track(accepted.File.Id.Value, RunAndReportAsync(accepted, lease, deferredBefore, ct));
        if (_gate is null)
        {
            await run.ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>Re-examines the resumed claims the gate deferred; an admitted one leaves the list and runs.</summary>
    private async Task RetryDeferredClaimsAsync(CancellationToken ct)
    {
        AcceptedJob[] waiting;
        lock (_deferredClaims)
        {
            waiting = [.. _deferredClaims];
        }

        foreach (AcceptedJob accepted in waiting)
        {
            if (await AdmitAndTrackClaimedAsync(accepted, ct).ConfigureAwait(false))
            {
                lock (_deferredClaims)
                {
                    _deferredClaims.Remove(accepted);
                }
            }
        }
    }

    /// <summary>Registers a run so <see cref="Status"/> counts it and <see cref="DrainAsync"/> waits for it; removed when it ends.</summary>
    private Task Track(string id, Task run)
    {
        _runs[id] = run;
        _ = run.ContinueWith(
            t =>
            {
                _ = t.Exception; // observed: the run logged its own failure
                _runs.TryRemove(id, out _);
                Publish();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        Publish();
        return run;
    }

    /// <summary>The handler call and the report for one claimed job; cancellation ends it without a report (the claim stays); the lease is always released.</summary>
    private async Task RunAndReportAsync(AcceptedJob accepted, IDisposable lease, int deferredBefore, CancellationToken ct)
    {
        try
        {
            await AnswerAsync(accepted, deferredBefore, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // the host asked us to stop: no report, the claim stays for the next start (spec 07)
        }
        finally
        {
            lease.Dispose();
        }
    }

    private async Task AnswerAsync(AcceptedJob accepted, int deferredBefore, CancellationToken ct)
    {
        JobReply reply;
        try
        {
            reply = await _handler(accepted.Job, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            using IDisposable? scope = Scope("report");
            LogHandlerFailed(accepted.File.Id.Value, ex.GetType().Name);
            return;
        }

        using (Scope("report"))
        {
            var fields = new ReportFields(
                reply.Status,
                reply.Reason,
                reply.Started,
                reply.Finished,
                (int)Math.Max(0, Math.Round((reply.Finished - reply.Started).TotalSeconds)),
                reply.CostUsd,
                reply.InputTokens,
                reply.OutputTokens,
                reply.NumTurns,
                reply.Model,
                reply.FilesChanged,
                reply.DiffRef);
            WriteOutcome report = await _bus.ReportAsync(accepted, fields, reply.Body, _signWith, ct).ConfigureAwait(false);
            if (report is WriteFailed failed)
            {
                LogWriteFailed("report", accepted.File.Id.Value, failed.Stage, failed.Detail);
                return;
            }

            _lastReportUtc = _clock.GetUtcNow();
            string status = EnvelopeWire.ToWire(reply.Status);
            LogReported(accepted.File.Id.Value, status, report is Deferred ? "deferred" : "pushed");
            await NoteDeferredAsync(report, deferredBefore, ct).ConfigureAwait(false);
        }
    }

    /// <summary>After a successful pull: the instant, the head and this machine's claimed count for <see cref="Status"/>.</summary>
    private async Task NotePulledAsync(CancellationToken ct)
    {
        _lastPullUtc = _clock.GetUtcNow();
        _busHeadSha = await _bus.HeadShaAsync(ct).ConfigureAwait(false);
        _jobsClaimedByMe = (await _bus.ListClaimedAsync(ct).ConfigureAwait(false)).Count;
        Publish();
    }

    private void Publish() =>
        Status = new ListenerStatus(State, _lastPollUtc, _lastPullUtc, _lastReportUtc, _busHeadSha, _runs.Count, _jobsClaimedByMe, _mode);

    /// <summary>After a pushed write, reports how many previously deferred commits went with it (§5, ex-OQ9).</summary>
    private async Task<int> NoteDeferredAsync(WriteOutcome outcome, int deferredBefore, CancellationToken ct)
    {
        int after = await _bus.UnpushedCommitCountAsync(ct).ConfigureAwait(false);
        if (outcome is Pushed && deferredBefore > 0 && after < deferredBefore)
        {
            LogPushedDeferred(deferredBefore - after);
        }

        return after;
    }

    private IDisposable? Scope(string phase)
    {
        if (!_log.IsEnabled(LogLevel.Information))
        {
            return null;
        }

        var scope = new Dictionary<string, object>
        {
            ["tenant"] = _bus.Me.Tenant.Value,
            ["machine"] = _bus.Me.Machine.Value,
            ["phase"] = phase,
        };
        return _log.BeginScope(scope);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "claimed job {Id} from {From} ({Push})")]
    private partial void LogClaimed(string id, string from, string push);

    [LoggerMessage(Level = LogLevel.Information, Message = "reported job {Id}: {Status} ({Push})")]
    private partial void LogReported(string id, string status, string push);

    [LoggerMessage(Level = LogLevel.Information, Message = "pushed {Count} deferred commit(s)")]
    private partial void LogPushedDeferred(int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "polling")]
    private partial void LogPhasePolling();

    [LoggerMessage(Level = LogLevel.Information, Message = "poll mode {Mode}")]
    private partial void LogPollMode(string mode);

    [LoggerMessage(Level = LogLevel.Information, Message = "resuming {Count} claimed job(s) from a previous run")]
    private partial void LogResuming(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "claimed job {Id} does not verify; left for the sweep")]
    private partial void LogClaimedNotVerifying(string id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "safety fetch: branch moved while the doorbell said unchanged; resynchronising")]
    private partial void LogSafetyFetchMoved();

    [LoggerMessage(Level = LogLevel.Warning, Message = "safety fetch failed: {Detail}")]
    private partial void LogSafetyFetchFailed(string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Phase} until {Until}: {Detail}")]
    private partial void LogPhaseBackoff(PollPhase phase, DateTimeOffset? until, string detail);

    [LoggerMessage(Level = LogLevel.Error, Message = "pull failed: {Detail}")]
    private partial void LogPullFailed(string detail);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Action} of job {Id} failed at {Stage}: {Detail}")]
    private partial void LogWriteFailed(string action, string id, string stage, string detail);

    [LoggerMessage(Level = LogLevel.Error, Message = "the handler threw {Exception} for job {Id}; the job stays claimed")]
    private partial void LogHandlerFailed(string id, string exception);

    /// <summary>The lease of the probe path (no gate): nothing to release.</summary>
    private sealed class NoopLease : IDisposable
    {
        public static NoopLease Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
