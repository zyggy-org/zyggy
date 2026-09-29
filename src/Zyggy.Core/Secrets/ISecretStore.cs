using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Secrets;

/// <summary>
/// The secret-store seam (founding spec §8, §9, §14): the only type through which Zyggy reads or writes secrets. Every
/// lookup is tenant-scoped; the full name is <c>zyggy/&lt;tenant&gt;/&lt;name&gt;</c>, so no call can reach another
/// tenant's secrets.
/// </summary>
public interface ISecretStore
{
    /// <summary>Reads a secret.</summary>
    /// <param name="tenant">The tenant that owns the secret.</param>
    /// <param name="name">The tenant-relative name, for example <c>hmac/1</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The secret bytes, or <see langword="null"/> when the secret is absent (absence never throws).</returns>
    Task<ReadOnlyMemory<byte>?> GetAsync(TenantId tenant, SecretName name, CancellationToken cancellationToken);

    /// <summary>Writes a secret, atomically replacing any existing value.</summary>
    /// <param name="tenant">The tenant that owns the secret.</param>
    /// <param name="name">The tenant-relative name.</param>
    /// <param name="value">The secret bytes; must not be empty.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the secret is stored.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="value"/> is empty.</exception>
    Task SetAsync(TenantId tenant, SecretName name, ReadOnlyMemory<byte> value, CancellationToken cancellationToken);

    /// <summary>Removes a secret.</summary>
    /// <param name="tenant">The tenant that owns the secret.</param>
    /// <param name="name">The tenant-relative name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when a secret was removed; <see langword="false"/> when it was absent.</returns>
    Task<bool> RemoveAsync(TenantId tenant, SecretName name, CancellationToken cancellationToken);
}
