using System.Text;

using Zyggy.Core.Envelope;
using Zyggy.Core.Secrets;

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
