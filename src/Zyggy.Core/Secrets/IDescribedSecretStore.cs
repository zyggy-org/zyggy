using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Secrets;

/// <summary>
/// A store that can name itself and say which layer currently provides a name (plan 09 A1): what <c>zyggy secret list</c>
/// prints in its <c>store</c> column and what the credentials-directory note of <c>secret set</c> relies on. Single-layer
/// stores answer their own name.
/// </summary>
internal interface IDescribedSecretStore
{
    /// <summary>The store's name, one of <see cref="SecretStoreNames"/>.</summary>
    string StoreName { get; }

    /// <summary>The layer that currently provides <paramref name="name"/> for <paramref name="tenant"/>; the store name for a single-layer store.</summary>
    ValueTask<string> SourceOfAsync(TenantId tenant, SecretName name, CancellationToken cancellationToken);
}
