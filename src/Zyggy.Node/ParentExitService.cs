using Zyggy.Core.Processes;

namespace Zyggy.Node;

/// <summary>
/// Spec 61 slice 8, <c>--exit-with-parent</c>: when the process that started the node ends (the scheduled task's wrapper, ended by
/// <c>Stop-ScheduledTask</c>), the node stops as on a normal host stop — its runs are ended by the runner, their claims kept, the listen
/// lock released — with exit code 0. A parent already gone at start stops the node at once.
/// </summary>
internal sealed class ParentExitService(ParentWatch watch, IHostApplicationLifetime lifetime) : BackgroundService
{
    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await watch.WaitForParentExitAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        lifetime.StopApplication();
    }
}
