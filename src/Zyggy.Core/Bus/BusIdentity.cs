using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Bus;

/// <summary>Who this node is on the bus: its tenant and machine name (founding spec §4 write rules, §14 tenancy). Both come from configuration; there is no default.</summary>
/// <param name="Tenant">The tenant whose prefix this node reads and writes.</param>
/// <param name="Machine">This machine's name — the <c>nodes/&lt;machine&gt;/</c> folder it owns and the <c>to</c> it accepts.</param>
public sealed record BusIdentity(TenantId Tenant, MachineName Machine);
