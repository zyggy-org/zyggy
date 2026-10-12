using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Configuration;

/// <summary>
/// The typed <c>node.json</c> a machine runs with (founding spec §10, the minimum of deliverable 04): who the node is, which
/// bus it reads, how often it polls, where its secrets live. There is no default tenant; the file names it or the node refuses
/// to start. The Node service extends this with its own keys (role, user, work hours, claude …), which this reader ignores.
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
    SecretsSettings Secrets);
