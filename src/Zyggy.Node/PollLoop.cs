using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;

namespace Zyggy.Node;

/// <summary>
/// The Node's one hosted service (founding spec §5, §6; spec 07): starts the <see cref="BusListener"/> (the §5 start-up rule — a
/// refused checkout, key or second listener ends the host with exit code 6 and one error line), resumes the node's own claims, then
/// runs the listener's loop with the work-hours mode selector until the host stops. It has no loop logic of its own: the body is
/// <see cref="BusListener"/>. A stop cancels the loop, waits for the interrupted runs to end (their claims stay for the next start),
/// logs how many were interrupted, and releases the listen lock. An unexpected exception ends the host with exit code 1.
/// </summary>
public sealed partial class PollLoop : BackgroundService
{
    private readonly BusListener _listener;
    private readonly PollModeSelector _mode;
    private readonly NodeStatus _status;
    private readonly NodeExitState _exit;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly NodeConfiguration _configuration;
    private readonly HealthServer _health;
    private readonly ClaudeAuthProbe _auth;
    private readonly ILogger<PollLoop> _log;
    private Task? _authTask;

    internal PollLoop(
        BusListener listener,
        PollModeSelector mode,
        NodeStatus status,
        NodeExitState exit,
        IHostApplicationLifetime lifetime,
        NodeConfiguration configuration,
        HealthServer health,
        ClaudeAuthProbe auth,
        ILogger<PollLoop> log)
    {
        _listener = listener;
        _mode = mode;
        _status = status;
        _exit = exit;
        _lifetime = lifetime;
        _configuration = configuration;
        _health = health;
        _auth = auth;
        _log = log;
    }

    /// <inheritdoc/>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        int interrupted = _listener.Status.JobsRunning;
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _listener.DrainAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // the host's shutdown timeout elapsed first; the runs are being killed with the process
        }

        if (interrupted > 0)
        {
            LogStopping(interrupted);
        }

        if (_authTask is { } auth)
        {
            await auth.ConfigureAwait(false);
        }

        await _health.DisposeAsync().ConfigureAwait(false);
        _listener.Dispose();
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _status.Listener = _listener;
        string version = NodeHost.InformationalVersion();
        LogNodeStarted(_configuration.Tenant.Value, _configuration.Machine.Value, version);
        try
        {
            BusStartupResult result = await _listener.StartAsync(stoppingToken).ConfigureAwait(false);
            if (result is not (BusStartupResult.Clean or BusStartupResult.Stashed))
            {
                LogRefusingToStart(result.ToString());
                Stop(NodeExitCodes.Startup);
                return;
            }

            _authTask = _auth.RunAsync(stoppingToken);
            try
            {
                await _health.StartAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                LogRefusingToStart("health port " + _health.ConfiguredPort.ToString(System.Globalization.CultureInfo.InvariantCulture) + " in use");
                Stop(NodeExitCodes.Startup);
                return;
            }

            await _listener.ResumeClaimedAsync(stoppingToken).ConfigureAwait(false);
            await _listener.RunAsync(_mode, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // a normal stop
        }
#pragma warning disable CA1031 // The host's contract: an unexpected fault is one error line and exit code 1, never an unobserved crash.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogFaulted(ex.GetType().Name);
            LogFaultDetail(ex.Message);
            Stop(NodeExitCodes.Faulted);
        }
    }

    private void Stop(int exitCode)
    {
        _exit.Code = exitCode;
        _lifetime.StopApplication();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "node started: tenant {Tenant}, machine {Machine}, version {Version}")]
    private partial void LogNodeStarted(string tenant, string machine, string version);

    [LoggerMessage(Level = LogLevel.Error, Message = "refusing to start: {Condition} — see README \"Node: refused to start\"")]
    private partial void LogRefusingToStart(string condition);

    [LoggerMessage(Level = LogLevel.Error, Message = "node faulted: {ExceptionType}")]
    private partial void LogFaulted(string exceptionType);

    [LoggerMessage(Level = LogLevel.Debug, Message = "fault detail: {Message}")]
    private partial void LogFaultDetail(string message);

    [LoggerMessage(Level = LogLevel.Information, Message = "stopping: {Count} job(s) interrupted; their claims stay for the next start")]
    private partial void LogStopping(int count);
}
