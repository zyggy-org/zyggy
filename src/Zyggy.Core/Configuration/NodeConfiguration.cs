using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Configuration;

/// <summary>
/// The typed <c>node.json</c> a machine runs with (founding spec §10, the minimum of deliverable 04): who the node is, which
/// bus it reads, how often it polls, where its secrets live. There is no default tenant; the file names it or the node refuses
/// to start. Deliverable 06 adds <c>claude</c>, <c>devRoots</c> and <c>nodeDir</c>; the Node service's other keys (role, user, work
/// hours …) are ignored by the reader.
/// </summary>
/// <param name="Tenant">The <c>tenant</c> — required.</param>
/// <param name="Machine">The <c>machine</c> — required.</param>
/// <param name="SignWith">The <c>signWith</c> key id (its tenant equals <paramref name="Tenant"/>); required by the verbs that sign, not by <c>reach</c>.</param>
/// <param name="Bus">The <c>bus</c> section.</param>
/// <param name="Poll">The <c>poll</c> section.</param>
/// <param name="Secrets">The <c>secrets</c> section.</param>
public sealed record NodeConfiguration(
    TenantId Tenant,
    MachineName Machine,
    KeyId? SignWith,
    BusSettings Bus,
    PollSettings Poll,
    SecretsSettings Secrets)
{
    /// <summary>Gets the <c>claude</c> section (spec 06); the §10 defaults when absent.</summary>
    public ClaudeSettings Claude { get; init; } = new();

    /// <summary>Gets the <c>devRoots</c> (absolute paths); read in 06, enforced with the registry (O18).</summary>
    public IReadOnlyList<string> DevRoots { get; init; } = [];

    /// <summary>Gets the <c>nodeDir</c> (an absolute path) that holds the node's <c>runs/</c> transcripts; <see langword="null"/> means the per-OS default.</summary>
    public string? NodeDir { get; init; }
}
