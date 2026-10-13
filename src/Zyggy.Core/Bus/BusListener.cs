using Microsoft.Extensions.Logging;

using Zyggy.Core.Envelope;

namespace Zyggy.Core.Bus;

/// <summary>
/// The receiver's loop (founding spec §5 poll loop), hosted by the probe verb now and by the Node service later: start-up
/// check, one pull, then per tick — wait until the poller says so, ring the doorbell, pull only when the branch moved, and
/// for every inbox file verify → claim → handler → report, or reject. One request in flight at a time; one log line per
/// state change and nothing on a silent tick (§11); every entry scoped with <c>tenant</c>, <c>machine</c> and <c>phase</c>.
/// </summary>
public sealed partial class BusListener
{
    private readonly IBusProvider _provider;
    private readonly BusRepository _bus;
    private readonly PollOptions _options;
    private readonly KeyId _signWith;
    private readonly TimeProvider _clock;
    private readonly Random _jitter;
    private readonly JobHandler _handler;
    private readonly ILogger<BusListener> _log;
    private bool _pullErrorLogged;

    /// <summary>Creates the listener.</summary>
    /// <param name="provider">The bus host seam.</param>
    /// <param name="bus">The repository layer for this node.</param>
    /// <param name="options">The poller's intervals.</param>
    /// <param name="signWith">The key this node signs reports with.</param>
    /// <param name="clock">The clock; the only time source.</param>
    /// <param name="jitter">The random source for the poll jitter.</param>
    /// <param name="handler">What to do with a verified, claimed job.</param>
    /// <param name="log">The logger.</param>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    public BusListener(
        IBusProvider provider,
        BusRepository bus,
        PollOptions options,
        KeyId signWith,
        TimeProvider clock,
        Random jitter,
        JobHandler handler,
        ILogger<BusListener> log)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _signWith = signWith ?? throw new ArgumentNullException(nameof(signWith));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _jitter = jitter ?? throw new ArgumentNullException(nameof(jitter));
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        State = PollState.Initial(clock.GetUtcNow());
    }

    /// <summary>Gets the poller's state: health for the Node service and the probe's status line.</summary>
    public PollState State { get; private set; }

    /// <summary>
    /// The §5 start-up rule, then one pull and one pass over what is already waiting. A dirty tree is stashed; a wrong branch,
    /// a missing repository or tenant directory, a rebase in progress or a missing signing key is returned without pulling.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>What the start-up check found.</returns>
    public async Task<BusStartupResult> StartAsync(CancellationToken ct)
    {
        BusStartupResult result = await _bus.EnsureCleanAsync(ct).ConfigureAwait(false);
        if (result is not (BusStartupResult.Clean or BusStartupResult.Stashed))
        {
            return result;
        }

        if (!await _bus.SignerCanSignAsync(_signWith, ct).ConfigureAwait(false))
        {
            return BusStartupResult.KeyMissing;
        }

        BusSyncResult pull = await _provider.PullAsync(ct).ConfigureAwait(false);
        if (!pull.Succeeded)
        {
            using IDisposable? scope = Scope("pull");
            LogPullFailed(pull.Detail);
        }

        await ProcessOnceAsync(ct).ConfigureAwait(false);
        State = PollState.Initial(_clock.GetUtcNow());
        return result;
    }

    /// <summary>
    /// One pass over the inbox: every file is verified before anything else; an accepted job is claimed, handed to the
    /// handler only when the claim is pushed or deferred, and answered with a signed report; a refused file is rejected.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How many inbox files were handled (claimed or rejected).</returns>
    public async Task<int> ProcessOnceAsync(CancellationToken ct)
    {
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
                    if (await ClaimAndAnswerAsync(accepted, ct).ConfigureAwait(false))
                    {
                        handled++;
                    }

                    deferredBefore = await _bus.UnpushedCommitCountAsync(ct).ConfigureAwait(false);
                    break;
                default:
                    break;
            }
        }

        return handled;
    }

    /// <summary>
    /// The loop: wait until the poller's next due instant, ring the doorbell, apply the outcome, pull and process when the
    /// branch moved; ends when <paramref name="ct"/> is cancelled.
    /// </summary>
    /// <param name="mode">The host's poll mode (the probe is always active).</param>
    /// <param name="ct">Cancellation token; cancellation ends the loop normally.</param>
    /// <returns>A task that completes when the loop ends.</returns>
    public async Task RunAsync(PollMode mode, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TimeSpan wait = State.NextDueUtc - _clock.GetUtcNow();
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, _clock, ct).ConfigureAwait(false);
                }

                HeadProbe outcome = await _provider.ProbeHeadAsync(State.ETag, ct).ConfigureAwait(false);
                PollTransition transition = State.Apply(outcome, mode, _clock.GetUtcNow(), (_jitter.NextDouble() * 2) - 1, _options);
                State = transition.State;
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

                if (!transition.Pull)
                {
                    continue;
                }

                using (Scope("pull"))
                {
                    BusSyncResult pull = await _provider.PullAsync(ct).ConfigureAwait(false);
                    if (!pull.Succeeded)
                    {
                        if (!_pullErrorLogged)
                        {
                            _pullErrorLogged = true;
                            LogPullFailed(pull.Detail);
                        }

                        continue;
                    }

                    _pullErrorLogged = false;
                }

                await ProcessOnceAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // the host asked us to stop
        }
    }

    private async Task<bool> ClaimAndAnswerAsync(AcceptedJob accepted, CancellationToken ct)
    {
        int deferredBefore = await _bus.UnpushedCommitCountAsync(ct).ConfigureAwait(false);
        WriteOutcome claim;
        using (Scope("claim"))
        {
            claim = await _bus.ClaimAsync(accepted, ct).ConfigureAwait(false);
            if (claim is WriteFailed failed)
            {
                LogWriteFailed("claim", accepted.File.Id.Value, failed.Stage, failed.Detail);
                return false;
            }

            LogClaimed(accepted.File.Id.Value, accepted.Job.Header.From.Value, claim is Deferred ? "deferred" : "pushed");
            deferredBefore = await NoteDeferredAsync(claim, deferredBefore, ct).ConfigureAwait(false);
        }

        JobReply reply;
        try
        {
            reply = await _handler(accepted.Job, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            using IDisposable? scope = Scope("report");
            LogHandlerFailed(accepted.File.Id.Value, ex.GetType().Name);
            return true;
        }

        using (Scope("report"))
        {
            var fields = new ReportFields(
                reply.Status,
                reply.Reason,
                reply.Started,
                reply.Finished,
                (int)Math.Max(0, Math.Round((reply.Finished - reply.Started).TotalSeconds)));
            WriteOutcome report = await _bus.ReportAsync(accepted, fields, reply.Body, _signWith, ct).ConfigureAwait(false);
            if (report is WriteFailed failed)
            {
                LogWriteFailed("report", accepted.File.Id.Value, failed.Stage, failed.Detail);
                return true;
            }

            string status = EnvelopeWire.ToWire(reply.Status);
            LogReported(accepted.File.Id.Value, status, report is Deferred ? "deferred" : "pushed");
            await NoteDeferredAsync(report, deferredBefore, ct).ConfigureAwait(false);
        }

        return true;
    }

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Phase} until {Until}: {Detail}")]
    private partial void LogPhaseBackoff(PollPhase phase, DateTimeOffset? until, string detail);

    [LoggerMessage(Level = LogLevel.Error, Message = "pull failed: {Detail}")]
    private partial void LogPullFailed(string detail);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Action} of job {Id} failed at {Stage}: {Detail}")]
    private partial void LogWriteFailed(string action, string id, string stage, string detail);

    [LoggerMessage(Level = LogLevel.Error, Message = "the handler threw {Exception} for job {Id}; the job stays claimed")]
    private partial void LogHandlerFailed(string id, string exception);
}
