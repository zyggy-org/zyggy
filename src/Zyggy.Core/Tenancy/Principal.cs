namespace Zyggy.Core.Tenancy;

/// <summary>
/// The <c>(tenant, user)</c> a Hub call runs under (founding spec §7, §14). It is resolved from the transport, never from a
/// request field.
/// </summary>
public sealed record Principal
{
    /// <summary>Creates a principal.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="user">The user inside that tenant.</param>
    /// <exception cref="ArgumentNullException">Thrown when either argument is null.</exception>
    public Principal(TenantId tenant, UserId user)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(user);
        Tenant = tenant;
        User = user;
    }

    /// <summary>Gets the tenant.</summary>
    public TenantId Tenant { get; }

    /// <summary>Gets the user.</summary>
    public UserId User { get; }
}
