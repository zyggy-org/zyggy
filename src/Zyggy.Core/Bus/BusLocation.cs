using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Bus;

/// <summary>
/// Where a repository-relative bus path points (founding spec §4 "Repository layout"), as read back by
/// <see cref="BusPaths.TryParse"/>. The tenant is always the one named in the path: parsing never assumes a tenant (§14).
/// </summary>
/// <param name="Tenant">The <c>&lt;org&gt;</c> segment of the path.</param>
/// <param name="Machine">The <c>&lt;machine&gt;</c> segment; <see langword="null"/> for the archive.</param>
/// <param name="Folder">The folder the path is in or names.</param>
/// <param name="Id">The envelope id when the path names a <c>&lt;ULID&gt;.md</c> file; otherwise <see langword="null"/>.</param>
public sealed record BusLocation(TenantId Tenant, MachineName? Machine, BusFolder Folder, EnvelopeId? Id);
