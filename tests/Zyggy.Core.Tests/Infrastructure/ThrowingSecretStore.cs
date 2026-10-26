using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Tests.Infrastructure;

/// <summary>A store that exists but cannot be opened (spec 09 AC-2): every member throws <see cref="Failure"/>; by default the Credential Manager's "no logon session".</summary>
public sealed class ThrowingSecretStore : ISecretStore, IDescribedSecretStore
{
    public Exception Failure { get; init; } = new SecretStoreUnavailableException("credential-manager", "1312 No such logon session");

    public string StoreName => "credential-manager";

    public Task<ReadOnlyMemory<byte>?> GetAsync(TenantId tenant, SecretName name, CancellationToken cancellationToken) => throw Failure;

    public Task SetAsync(TenantId tenant, SecretName name, ReadOnlyMemory<byte> value, CancellationToken cancellationToken) => throw Failure;

    public Task<bool> RemoveAsync(TenantId tenant, SecretName name, CancellationToken cancellationToken) => throw Failure;

    public Task<IReadOnlyList<SecretName>> ListAsync(TenantId tenant, CancellationToken cancellationToken) => throw Failure;

    public ValueTask<string> SourceOfAsync(TenantId tenant, SecretName name, CancellationToken cancellationToken) => throw Failure;
}
