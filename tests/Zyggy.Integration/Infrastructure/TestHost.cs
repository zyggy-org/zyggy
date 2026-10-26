using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Envelope;
using Zyggy.Core.Jobs;
using Zyggy.Core.Processes;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Integration.Infrastructure;

/// <summary>
/// The production services for one bus checkout (spec 04 Steps 5 and 8): the real process runner (optionally decorated with
/// <see cref="HookedProcessRunner"/>), the git client with the fixture's isolated configuration, the envelope signer, a secret
/// store holding the synthetic PAT and the two golden keys, the GitHub bus provider whose head check is answered by
/// <see cref="BareRepoHeadHandler"/> from the bare repository, and the <see cref="BusRepository"/> with jitter fixed to 0.
/// </summary>
internal static class TestHost
{
    public static readonly BusRemote Remote = new("acme-org", "acme-bus", "main", new Uri("https://example.invalid/acme-org/acme-bus.git"), null);

    /// <summary>Builds the services for <paramref name="machine"/> working in the fixture's node clone (or <paramref name="checkoutRoot"/>).</summary>
    public static ServiceProvider Build(
        BusRepoFixture bus,
        MachineName machine,
        ISecretStore store,
        HookedProcessRunner? hooks = null,
        string? checkoutRoot = null,
        ILoggerProvider? logging = null,
        Action<GitHubBusOptions>? configure = null)
    {
        var services = new ServiceCollection()
            .AddProcessRunner()
            .AddEnvelopeSigning()
            .AddSingleton(store);
        if (hooks is not null)
        {
            services.AddSingleton<IProcessRunner>(hooks);
        }

        if (logging is not null)
        {
            services.AddLogging(b => b.AddProvider(logging).SetMinimumLevel(LogLevel.Trace));
        }

        services.AddGitHubBus(o =>
        {
            o.Remote = Remote;
            o.Identity = new BusIdentity(bus.Tenant, machine);
            o.CheckoutRoot = checkoutRoot ?? bus.CloneDir;
            o.Version = "0.0.0-test";
            o.HandlerFactory = () => new BareRepoHeadHandler(ct => HeadShaOrNullAsync(bus, ct));
            o.GitEnvironment = bus.GitEnvironment;
            o.Jitter = () => 0;
            configure?.Invoke(o);
        });
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Builds the services with a <see cref="BusListener"/> for <paramref name="machine"/>: the probe handler (or <paramref name="handler"/>),
    /// 1 s poll intervals, a seeded <see cref="Random"/>, signing with <c>acme/1</c>.
    /// </summary>
    public static ServiceProvider BuildListener(
        BusRepoFixture bus,
        MachineName machine,
        ISecretStore store,
        JobHandler? handler = null,
        HookedProcessRunner? hooks = null,
        ILoggerProvider? logging = null)
    {
        var services = new ServiceCollection()
            .AddProcessRunner()
            .AddEnvelopeSigning()
            .AddSingleton(store)
            .AddSingleton(handler ?? Zyggy.Core.Bus.Probe.ProbeHandler.For(machine, TimeProvider.System))
            .AddSingleton(TestKeys.Acme1)
            .AddSingleton(new Random(7))
            .AddSingleton(PollOptions.Default with { ActiveInterval = TimeSpan.FromSeconds(1), Jitter = TimeSpan.FromMilliseconds(200) });
        if (hooks is not null)
        {
            services.AddSingleton<IProcessRunner>(hooks);
        }

        if (logging is not null)
        {
            services.AddLogging(b => b.AddProvider(logging).SetMinimumLevel(LogLevel.Trace));
        }

        services.AddGitHubBus(o =>
        {
            o.Remote = Remote;
            o.Identity = new BusIdentity(bus.Tenant, machine);
            o.CheckoutRoot = bus.CloneDir;
            o.Version = "0.0.0-test";
            o.HandlerFactory = () => new BareRepoHeadHandler(ct => HeadShaOrNullAsync(bus, ct));
            o.GitEnvironment = bus.GitEnvironment;
            o.Jitter = () => 0;
        });
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Builds the production composition of a node's job path (plan 06 Step 9): the real process runner, the Claude Code runner
    /// pointed at <paramref name="claudePath"/> (the fake), the policy from <paramref name="configuration"/>, the job runner as the
    /// listener's handler, and the same bus as <see cref="BuildListener"/>; <paramref name="withDispatcher"/> adds the §6 dispatcher as the listener's gate (plan 07). <paramref name="jobRunnerAfterBus"/> registers the job runner after
    /// the bus instead of before it (the order must not matter, plan 06 Assumption A2).
    /// </summary>
    public static ServiceProvider BuildRunnerListener(
        BusRepoFixture bus,
        MachineName machine,
        ISecretStore store,
        string claudePath,
        NodeConfiguration configuration,
        Action<JobRunnerOptions>? runner = null,
        HookedProcessRunner? hooks = null,
        ILoggerProvider? logging = null,
        bool jobRunnerAfterBus = false,
        bool withDispatcher = false)
    {
        // Plan 07 Step 8: the production composition root; the test's overrides follow (last registration wins for the store, the options are merged).
        var services = new ServiceCollection()
            .AddNodeServices(configuration with { Claude = configuration.Claude with { Path = claudePath } })
            .AddSingleton(store)
            .AddSingleton(new Random(7))
            .AddSingleton(PollOptions.Default with { ActiveInterval = TimeSpan.FromSeconds(1), Jitter = TimeSpan.FromMilliseconds(200) });
        if (runner is not null)
        {
            services.Configure(runner);
        }

        if (hooks is not null)
        {
            services.AddSingleton<IProcessRunner>(hooks);
        }

        if (logging is not null)
        {
            services.AddLogging(b => b.AddProvider(logging).SetMinimumLevel(LogLevel.Trace));
        }

        if (jobRunnerAfterBus)
        {
            // Plan 06 Assumption A2: the order of AddJobRunner and AddGitHubBus must not matter — register the runner again, after the bus.
            services.AddJobRunner(runner);
        }

        if (withDispatcher)
        {
            // Plan 07 Step 4: the §6 admission rules as the listener's gate.
            services.AddJobDispatcher();
        }

        services.Configure<GitHubBusOptions>(o =>
        {
            o.Version = "0.0.0-test";
            o.HandlerFactory = () => new BareRepoHeadHandler(ct => HeadShaOrNullAsync(bus, ct));
            o.GitEnvironment = bus.GitEnvironment;
            o.Jitter = () => 0;
        });
        return services.BuildServiceProvider();
    }

    /// <summary>The typed <c>node.json</c> of <paramref name="machine"/> on the fixture's bus, with an optional <c>claude</c> section.</summary>
    public static NodeConfiguration NodeConfigurationFor(BusRepoFixture bus, MachineName machine, ClaudeSettings? claude = null, string? nodeDir = null) =>
        new(bus.Tenant, machine, TestKeys.Acme1, new BusSettings(Remote.Owner, Remote.Repository, Remote.Branch, bus.CloneDir, Remote.HttpsUrl!.ToString()), new PollSettings(1, 1, 1), new SecretsSettings(SecretStoreKind.File))
        {
            Claude = claude ?? new ClaudeSettings(),
            NodeDir = nodeDir,
        };

    /// <summary>A hooked runner over the production one, for the push-retry scenarios.</summary>
    public static HookedProcessRunner Hooks()
    {
        using ServiceProvider inner = new ServiceCollection().AddProcessRunner().BuildServiceProvider();
        return new HookedProcessRunner(inner.GetRequiredService<IProcessRunner>());
    }

    /// <summary>A store with the synthetic PAT and both golden keys for the fixture's tenant.</summary>
    public static async Task<InMemorySecretStore> StoreAsync(TenantId tenant, CancellationToken ct)
    {
        InMemorySecretStore store = await TestKeys.StoreWithBothAsync(ct);
        await store.SetAsync(tenant, TestKeys.GitHubPat, System.Text.Encoding.UTF8.GetBytes(TestKeys.TestPat), ct);
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
