using Microsoft.Extensions.Logging;

using Zyggy.Core.Memory;

namespace Zyggy.Core.Bus;

/// <summary>What one due spool became.</summary>
/// <param name="File">The spool.</param>
/// <param name="Result">The submit outcome, or <see langword="null"/> when no line was left to ship.</param>
/// <param name="DroppedPatterns">The name of the pattern that dropped each secret-shaped line, in spool order.</param>
public sealed record FlushOutcome(SpoolFile File, SubmitResult? Result, IReadOnlyList<string> DroppedPatterns);

/// <summary>
/// The laptop's <c>SessionEnd</c> hook (spec 12 D2): ships every due spool as one signed context envelope — the ending session's,
/// then the stale ones a crashed session left — dropping any secret-shaped line by pattern name. A spool is deleted only once its
/// envelope is on the bus or committed locally; a failed or refused ship keeps it for the next flush. Never throws except cancellation.
/// </summary>
public sealed partial class ContextFlusher
{
    private readonly ContextSpool _spool;
    private readonly ContextSubmitter _submitter;
    private readonly SecretPatterns _patterns;
    private readonly TimeProvider _clock;
    private readonly ILogger<ContextFlusher> _log;

    internal ContextFlusher(ContextSpool spool, ContextSubmitter submitter, SecretPatterns patterns, TimeProvider clock, ILogger<ContextFlusher> log)
    {
        _spool = spool ?? throw new ArgumentNullException(nameof(spool));
        _submitter = submitter ?? throw new ArgumentNullException(nameof(submitter));
        _patterns = patterns ?? throw new ArgumentNullException(nameof(patterns));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>Ships the due spools (<see cref="ContextSpool.Due"/>), every one even after a failure.</summary>
    /// <param name="sessionId">The ending session, or <see langword="null"/> for the stale spools only.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>One outcome per due spool, in flush order.</returns>
    public async Task<IReadOnlyList<FlushOutcome>> FlushAsync(string? sessionId, CancellationToken ct)
    {
        var outcomes = new List<FlushOutcome>();
        foreach (SpoolFile file in _spool.Due(sessionId, _clock.GetUtcNow()))
        {
            ct.ThrowIfCancellationRequested();
            outcomes.Add(await ShipAsync(file, ct).ConfigureAwait(false));
        }

        return outcomes;
    }

    private async Task<FlushOutcome> ShipAsync(SpoolFile file, CancellationToken ct)
    {
        var kept = new List<string>(file.Lines.Count);
        var dropped = new List<string>();
        foreach (string line in file.Lines)
        {
            if (_patterns.TryMatch(line, out string name))
            {
                LogDropped(name);
                dropped.Add(name);
            }
            else
            {
                kept.Add(line);
            }
        }

        string session8 = file.SessionId.Length > 8 ? file.SessionId[..8] : file.SessionId;
        if (kept.Count == 0)
        {
            _spool.Delete(file);
            return new FlushOutcome(file, null, dropped);
        }

        SubmitResult result = await _submitter.SubmitAsync(new ContextRequest(file.Scope, kept), ct).ConfigureAwait(false);
        switch (result)
        {
            case Submitted submitted:
                _spool.Delete(file);
                LogShipped(submitted.Id.Value, session8);
                break;
            case SubmitDeferred deferred:
                _spool.Delete(file);
                LogShipped(deferred.Id.Value, session8);
                break;
            case SubmitFailed failed:
                LogKept(session8, failed.Stage);
                break;
            case SubmitRefused refused:
                LogKept(session8, refused.Field);
                break;
            case ContextRefused byPattern:
                LogKept(session8, byPattern.PatternName);
                break;
            default:
                LogKept(session8, result.GetType().Name);
                break;
        }

        return new FlushOutcome(file, result, dropped);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "context flush: line dropped (pattern {Name})")]
    private partial void LogDropped(string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "shipped context {Id} for session {Session}")]
    private partial void LogShipped(string id, string session);

    [LoggerMessage(Level = LogLevel.Warning, Message = "context flush: spool of session {Session} kept ({Stage})")]
    private partial void LogKept(string session, string stage);
}
