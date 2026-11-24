using Microsoft.Extensions.Logging;

namespace Zyggy.Core.Processes;

/// <summary>
/// Spec 61 slice 8 (<c>zyggy-node --exit-with-parent</c>): watches the process that started this one — the scheduled task's
/// <c>powershell.exe</c> wrapper on a laptop — and returns once it is gone, so the node stops as on a normal host stop (runs ended by the
/// runner, claims kept, lock released). Gone means: no parent known, the Linux init process (an orphan's parent), no process under the
/// pid, or a process under it that started later than the one first seen (the pid was reused; Windows keeps reporting the old parent
/// pid). It looks once a second on the injected clock.
/// </summary>
internal sealed partial class ParentWatch(IProcessTable processes, Func<int, int?> parentOf, TimeProvider clock, ILogger<ParentWatch> log)
{
    /// <summary>How often the parent is looked at.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(1);

    /// <summary>Returns when the parent process is gone; throws <see cref="OperationCanceledException"/> when <paramref name="ct"/> is cancelled first.</summary>
    public async Task WaitForParentExitAsync(CancellationToken ct)
    {
        ProcessFacts self = processes.Self();
        int? parentPid = parentOf(self.Pid);
        ProcessFacts? parent = parentPid is { } pid && pid > 1 ? processes.Find(pid) : null;
        if (parent is null || (parent.StartedUtc is { } p && self.StartedUtc is { } s && p > s + Tolerance))
        {
            LogGoneAtStart(parentPid ?? 0);
            return;
        }

        LogWatching(parent.Pid, parent.Name);
        while (true)
        {
            await Task.Delay(Interval, clock, ct).ConfigureAwait(false);
            ProcessFacts? now = processes.Find(parent.Pid);
            bool gone = now is null
                || (parent.StartedUtc is { } first && (now.StartedUtc is not { } seen || (seen - first).Duration() > Tolerance));
            if (gone)
            {
                LogEnded(parent.Pid, parent.Name);
                return;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "--exit-with-parent: the process that started the node (pid {Pid}) is already gone; stopping")]
    private partial void LogGoneAtStart(int pid);

    [LoggerMessage(Level = LogLevel.Debug, Message = "--exit-with-parent: watching pid {Pid} ({Name})")]
    private partial void LogWatching(int pid, string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "--exit-with-parent: the process that started the node (pid {Pid}, {Name}) ended; stopping the node, its runs are ended and their claims kept")]
    private partial void LogEnded(int pid, string name);
}
