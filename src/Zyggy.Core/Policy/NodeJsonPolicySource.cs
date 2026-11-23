using Zyggy.Core.Configuration;

namespace Zyggy.Core.Policy;

/// <summary>
/// The v1 <see cref="IPolicySource"/>: the machine's <c>node.json</c>, projected into an immutable <see cref="NodePolicy"/>. Every key is the
/// one read at start except <c>devRoots</c>, which comes from the <see cref="SettingsInUse"/> each time the policy is asked for (spec 55).
/// </summary>
internal sealed class NodeJsonPolicySource : IPolicySource
{
    private readonly NodePolicy _policy;
    private readonly SettingsInUse _settings;

    public NodeJsonPolicySource(NodeConfiguration configuration)
        : this(configuration, new SettingsInUse(configuration))
    {
    }

    public NodeJsonPolicySource(NodeConfiguration configuration, SettingsInUse settings)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        var claude = configuration.Claude;
        _policy = new NodePolicy(
            [.. claude.EffectiveAllowedTools],
            claude.DefaultTimeoutMinutes,
            claude.MaxTurns,
            claude.MaxBudgetUsd,
            [],
            configuration.MaxConcurrentJobs,
            WorkHours.FromSettings(configuration.WorkHours))
        {
            Commands = configuration.Commands,
            FetchMaxBytes = configuration.FetchMaxBytes,
            WorkMail = Outlook.OutlookSettings.FromNodeJson(configuration.Outlook).Enabled,
            Models = claude.Models,
        };
    }

    public Task<NodePolicy> GetAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_policy with { DevRoots = _settings.Current.DevRoots });
    }
}
