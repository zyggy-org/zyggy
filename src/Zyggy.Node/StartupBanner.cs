using Zyggy.Core.Configuration;

namespace Zyggy.Node;

/// <summary>Plan 07 Step 1's placeholder hosted service: logs the <c>node started</c> line once and nothing else; replaced by <c>PollLoop</c> in Step 9.</summary>
internal sealed class StartupBanner(NodeConfiguration configuration, ILoggerFactory loggerFactory) : IHostedService
{
    private readonly ILogger _logger = loggerFactory.CreateLogger(typeof(NodeHost).FullName!);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        string version = NodeHost.InformationalVersion();
        NodeLog.NodeStarted(_logger, configuration.Tenant.Value, configuration.Machine.Value, version);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
