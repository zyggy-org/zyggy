using System.Text;

using Zyggy.Core.Envelope;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Tests.Infrastructure;

/// <summary>
/// The two synthetic golden test keys fixed by _specs/03-envelope-signing.md (also listed in tests/golden/README.md).
/// The only place in the test code that holds the secret text.
/// </summary>
public static class TestKeys
{
    public static KeyId Acme1 { get; } = KeyId.Parse("acme/1");

    public static KeyId Acme2 { get; } = KeyId.Parse("acme/2");

    public static byte[] Secret1 => Encoding.UTF8.GetBytes("zyggy-golden-test-key-acme-00001");

    public static byte[] Secret2 => Encoding.UTF8.GetBytes("zyggy-golden-test-key-acme-00002");

    /// <summary>The one PAT-like string in the repository (spec 04): deliberately not token-shaped (no <c>ghp_</c> / <c>github_pat_</c> prefix).</summary>
    public static string TestPat => "zyggy-test-pat-0001";

    public static SecretName GitHubPat { get; } = SecretName.Parse("github-pat");

    /// <summary>A store holding the synthetic PAT for <paramref name="tenant"/>.</summary>
    public static async Task<InMemorySecretStore> StoreWithPatAsync(TenantId tenant, CancellationToken ct)
    {
        var store = new InMemorySecretStore();
        await store.SetAsync(tenant, GitHubPat, Encoding.UTF8.GetBytes(TestPat), ct);
        return store;
    }

    public static async Task<InMemorySecretStore> StoreWithBothAsync(CancellationToken ct)
    {
        var store = new InMemorySecretStore();
        await store.SetAsync(Acme1.Tenant, Acme1.SecretName, Secret1, ct);
        await store.SetAsync(Acme2.Tenant, Acme2.SecretName, Secret2, ct);
        return store;
    }

    public static async Task<InMemorySecretStore> StoreWithAsync(KeyId keyId, CancellationToken ct)
    {
        var store = new InMemorySecretStore();
        await store.SetAsync(keyId.Tenant, keyId.SecretName, keyId.Number == 2 ? Secret2 : Secret1, ct);
        return store;
    }
}
