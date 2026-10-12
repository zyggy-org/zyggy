using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Git;
using Zyggy.Core.Processes;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;

namespace Zyggy.Integration.Infrastructure;

/// <summary>
/// The production services for one bus checkout (spec 04 Step 5): the real process runner and git client (isolated git
/// configuration from the fixture), the envelope signer, an in-memory secret store holding the synthetic PAT and the two golden
/// keys, and the GitHub bus provider whose head check is answered by <see cref="BareRepoHeadHandler"/> from the bare repository.
/// </summary>
internal static class TestHost
{
    public static readonly BusRemote Remote = new("acme-org", "acme-bus", "main", new Uri("https://example.invalid/acme-org/acme-bus.git"), null);

    /// <summary>Builds the services; the secret store is registered as <see cref="ISecretStore"/> and also resolvable as <see cref="InMemorySecretStore"/>.</summary>
    public static ServiceProvider Build(BusRepoFixture bus, MachineName machine, InMemorySecretStore store, Action<GitHubBusOptions>? configure = null)
    {
        var services = new ServiceCollection()
            .AddProcessRunner()
            .AddEnvelopeSigning()
            .AddSingleton(store)
            .AddSingleton<ISecretStore>(store);
        services.AddSingleton(sp => new GitClient(
            sp.GetRequiredService<IProcessRunner>(),
            new GitClientOptions { Environment = bus.GitEnvironment },
            sp.GetRequiredService<TimeProvider>()));
        services.AddGitHubBus(o =>
        {
            o.Remote = Remote;
            o.Identity = new BusIdentity(bus.Tenant, machine);
            o.CheckoutRoot = bus.CloneDir;
            o.Version = "0.0.0-test";
            o.HandlerFactory = () => new BareRepoHeadHandler(ct => HeadShaOrNullAsync(bus, ct));
            configure?.Invoke(o);
        });
        return services.BuildServiceProvider();
    }

    /// <summary>A store with the synthetic PAT and both golden keys for the fixture's tenant.</summary>
    public static async Task<InMemorySecretStore> StoreAsync(TenantId tenant, CancellationToken ct)
    {
        InMemorySecretStore store = await Zyggy.Core.Tests.Infrastructure.TestKeys.StoreWithBothAsync(ct);
        await store.SetAsync(tenant, Zyggy.Core.Tests.Infrastructure.TestKeys.GitHubPat, System.Text.Encoding.UTF8.GetBytes(Zyggy.Core.Tests.Infrastructure.TestKeys.TestPat), ct);
        return store;
    }

    private static async Task<string?> HeadShaOrNullAsync(BusRepoFixture bus, CancellationToken ct)
    {
        try
        {
            return await bus.HeadShaAsync(ct);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
