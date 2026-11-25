using System.Collections.Concurrent;

using Zyggy.Core.Envelope;

namespace Zyggy.Core.Jobs;

/// <summary>
/// Spec 61 slice 5: the signal between the node's listener and its job runner that a running job was withdrawn. The listener raises it
/// for a verified withdrawal of a live run; the runner hands it to the model like its own limit, so the session's process tree is killed
/// and the run ends with its partial text, its worktree finished as for a failed run. One singleton per node (registered with the runner),
/// never static state.
/// </summary>
internal sealed class RunWithdrawals
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _signals = new(StringComparer.Ordinal);

    /// <summary>The signal of <paramref name="job"/>'s run: cancelled once the job is withdrawn (also when that happened before the run asked).</summary>
    /// <param name="job">The job id.</param>
    /// <returns>The token.</returns>
    internal CancellationToken For(EnvelopeId job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return Signal(job).Token;
    }

    /// <summary>Raises the signal of <paramref name="job"/>'s run.</summary>
    /// <param name="job">The job id.</param>
    internal void Withdraw(EnvelopeId job)
    {
        ArgumentNullException.ThrowIfNull(job);
        Signal(job).Cancel();
    }

    /// <summary>Forgets <paramref name="job"/>'s signal once its run has ended, so a later run of the id starts unsignalled.</summary>
    /// <param name="job">The job id.</param>
    internal void Close(EnvelopeId job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (_signals.TryRemove(job.Value, out CancellationTokenSource? signal))
        {
            signal.Dispose();
        }
    }

    private CancellationTokenSource Signal(EnvelopeId job) => _signals.GetOrAdd(job.Value, static _ => new CancellationTokenSource());
}
