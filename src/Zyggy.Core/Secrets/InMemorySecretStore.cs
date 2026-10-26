using System.Collections.Concurrent;

using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Secrets;

/// <summary>
/// An <see cref="ISecretStore"/> held in memory by one instance: the substitute for unit and integration tests. Values are
/// copied on write and on read.
/// </summary>
public sealed class InMemorySecretStore : ISecretStore, IDescribedSecretStore
{
    private readonly ConcurrentDictionary<(TenantId Tenant, SecretName Name), byte[]> _secrets = new();

    string IDescribedSecretStore.StoreName => SecretStoreNames.Memory;

    /// <inheritdoc />
    public Task<ReadOnlyMemory<byte>?> GetAsync(TenantId tenant, SecretName name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(name);
        cancellationToken.ThrowIfCancellationRequested();
        // Explicit branches: a null byte[] would convert implicitly to an empty, non-null ReadOnlyMemory<byte>.
        if (!_secrets.TryGetValue((tenant, name), out byte[]? stored))
        {
            return Task.FromResult<ReadOnlyMemory<byte>?>(null);
        }

        return Task.FromResult<ReadOnlyMemory<byte>?>(stored.ToArray());
    }

    /// <inheritdoc />
    public Task SetAsync(TenantId tenant, SecretName name, ReadOnlyMemory<byte> value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(name);
        if (value.IsEmpty)
        {
            throw new ArgumentException("A secret value must not be empty.", nameof(value));
        }

        cancellationToken.ThrowIfCancellationRequested();
        _secrets[(tenant, name)] = value.ToArray();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> RemoveAsync(TenantId tenant, SecretName name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(name);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_secrets.TryRemove((tenant, name), out _));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SecretName>> ListAsync(TenantId tenant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<SecretName> names = _secrets.Keys
            .Where(k => k.Tenant == tenant)
            .Select(k => k.Name)
            .OrderBy(n => n.Value, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(names);
    }

    ValueTask<string> IDescribedSecretStore.SourceOfAsync(TenantId tenant, SecretName name, CancellationToken cancellationToken) =>
        ValueTask.FromResult(SecretStoreNames.Memory);
}
