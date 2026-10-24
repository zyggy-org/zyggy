namespace Zyggy.Node;

/// <summary>
/// The few knobs of the host that are not in <c>node.json</c> (spec 07 Contracts → Hosting): tests change them through
/// <see cref="NodeHost.Build"/>'s <c>configure</c>; production never does.
/// </summary>
public sealed class NodeHostOptions
{
    /// <summary>Gets or sets the loopback port of <c>/health</c> (§11: 4711); <c>0</c> binds an ephemeral port — tests only.</summary>
    public int HealthPort { get; set; } = 4711;

    /// <summary>Gets or sets how long a stop may take before the host gives up on running work (the model processes are killed by the runner).</summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Gets or sets how often <c>claude auth status</c> is re-probed for the <c>claudeAuthOk</c> health field (spec 07 ex-Q3).</summary>
    public TimeSpan AuthProbeInterval { get; set; } = TimeSpan.FromHours(1);
}
