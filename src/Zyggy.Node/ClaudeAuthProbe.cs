using Microsoft.Extensions.Options;

using Zyggy.Core.Models;

namespace Zyggy.Node;

/// <summary>
/// Keeps <see cref="NodeStatus.ClaudeAuthOk"/> current (founding spec §11 as amended by A7-4; spec 07 ex-Q3): probes through the
/// model seam at start, every <see cref="NodeHostOptions.AuthProbeInterval"/>, and when a run failed with an authentication error.
/// Logs once per transition: <c>claude auth ok</c> on the first <see langword="true"/>, <c>claude auth expired</c> on
/// <see langword="true"/> → <see langword="false"/>, and <c>claude auth status unknown</c> once per streak of "could not tell".
/// </summary>
internal sealed partial class ClaudeAuthProbe : IDisposable
{
    private readonly IModelRunner _model;
    private readonly NodeStatus _status;
    private readonly TimeProvider _clock;
    private readonly IOptions<NodeHostOptions> _options;
    private readonly ILogger<ClaudeAuthProbe> _log;
    private readonly SemaphoreSlim _requests = new(0);
    private bool _unknownLogged;

    public ClaudeAuthProbe(IModelRunner model, NodeStatus status, TimeProvider clock, IOptions<NodeHostOptions> options, ILogger<ClaudeAuthProbe> log)
    {
        _model = model;
        _status = status;
        _clock = clock;
        _options = options;
        _log = log;
    }

    /// <summary>Asks for a probe at the loop's next wake (after a run that failed with <c>auth</c>).</summary>
    public void RequestProbe()
    {
        if (_requests.CurrentCount == 0)
        {
            _requests.Release();
        }
    }

    /// <summary>One probe; the verdict replaces the status (a <see langword="null"/> answer leaves it).</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the status is updated.</returns>
    public async Task ProbeAsync(CancellationToken ct)
    {
        bool? verdict = await _model.CheckAuthAsync(ct).ConfigureAwait(false);
        bool? previous = _status.ClaudeAuthOk;
        switch (verdict)
        {
            case null:
                if (!_unknownLogged)
                {
                    _unknownLogged = true;
                    LogUnknown();
                }

                return;
            case true when previous is not true:
                LogOk();
                break;
            case false when previous is true:
                LogExpired();
                break;
            default:
                break;
        }

        _unknownLogged = false;
        _status.ClaudeAuthOk = verdict;
    }

    /// <summary>The probe at start, then on every interval tick or request, until <paramref name="ct"/> is cancelled.</summary>
    /// <param name="ct">Cancellation token; cancellation ends the loop normally.</param>
    /// <returns>A task that completes when the loop ends.</returns>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await ProbeAsync(ct).ConfigureAwait(false);
            using var timer = new PeriodicTimer(_options.Value.AuthProbeInterval, _clock);
            while (!ct.IsCancellationRequested)
            {
                Task tick = timer.WaitForNextTickAsync(ct).AsTask();
                Task request = _requests.WaitAsync(ct);
                await Task.WhenAny(tick, request).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                await ProbeAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // the host asked us to stop
        }
    }

    public void Dispose() => _requests.Dispose();

    [LoggerMessage(Level = LogLevel.Information, Message = "claude auth ok")]
    private partial void LogOk();

    [LoggerMessage(Level = LogLevel.Warning, Message = "claude auth expired")]
    private partial void LogExpired();

    [LoggerMessage(Level = LogLevel.Warning, Message = "claude auth status unknown")]
    private partial void LogUnknown();
}
