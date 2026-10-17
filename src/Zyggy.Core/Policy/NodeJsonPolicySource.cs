using Zyggy.Core.Configuration;

namespace Zyggy.Core.Policy;

/// <summary>The v1 <see cref="IPolicySource"/>: the machine's <c>node.json</c>, projected once into an immutable <see cref="NodePolicy"/>.</summary>
internal sealed class NodeJsonPolicySource : IPolicySource
{
    private readonly NodePolicy _policy;

    public NodeJsonPolicySource(NodeConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var claude = configuration.Claude;
        _policy = new NodePolicy(
            [.. claude.EffectiveAllowedTools],
            claude.DefaultTimeoutMinutes,
            claude.MaxTurns,
            claude.MaxBudgetUsd,
            [.. configuration.DevRoots],
            configuration.MaxConcurrentJobs,
            WorkHours.FromSettings(configuration.WorkHours));
    }

    public Task<NodePolicy> GetAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_policy);
    }
}
