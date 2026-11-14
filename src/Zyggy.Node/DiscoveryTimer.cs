using Zyggy.Core.Bus;
using Zyggy.Core.Health;
using Zyggy.Core.Registry;

namespace Zyggy.Node;

/// <summary>
/// Schedules the node's discovery cycle (spec 14, plan A3): the cycle itself is Core's <see cref="DiscoveryLoop"/>. This service only wires
/// the node's own <c>/health</c> verdict into the published file and makes the first publish wait for the listener to be up and polled (A2).
/// The <c>zyggy-core</c> commit of <c>/health</c> is re-read on the same interval (spec 15).
/// </summary>
internal sealed class DiscoveryTimer : BackgroundService
{
    private static readonly TimeSpan ReadyBound = TimeSpan.FromSeconds(60);

    private readonly DiscoveryLoop _loop;
    private readonly AgentCoreCommit _agentCore;

    public DiscoveryTimer(DiscoveryLoop loop, AgentCoreCommit agentCore, NodeStatus status, HealthServer health, PollOptions poll, TimeProvider clock)
    {
        _loop = loop;
        _agentCore = agentCore;
        loop.Health = () =>
        {
            HealthLevel level = HealthRules.Evaluate(
                health.Snapshot(),
                clock.GetUtcNow(),
                poll,
                NodeHost.InformationalVersion(),
                "http://localhost:" + health.ConfiguredPort.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/health").Level;
            return level == HealthLevel.Down ? null : level;
        };
        loop.BeforeFirstPublish = ct => status.WhenReadyAsync(clock, ReadyBound, ct);
    }

    /// <inheritdoc/>
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(_loop.RunAsync(stoppingToken), _agentCore.RunAsync(stoppingToken));
}
