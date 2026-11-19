using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Jobs;
using Zyggy.Core.Memory;
using Zyggy.Core.Models;
using Zyggy.Core.Policy;
using Zyggy.Core.Processes;
using Zyggy.Core.Registry;
using Zyggy.Core.Secrets;

namespace Zyggy.Core.Configuration;

/// <summary>The one composition root of a node's services from its <c>node.json</c> (spec 07 ex-Q8): the Node service, the CLI's bus verbs and the integration tests all build from it.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the production services for <paramref name="configuration"/>: the configuration itself, the process runner, the file
    /// secret store rooted at <c>secrets.root</c>, envelope signing, the <see cref="PollOptions"/> from <c>poll</c>, the signing
    /// <see cref="KeyId"/> when <c>signWith</c> is set, the Claude Code model runner at <c>claude.path</c>, the <c>node.json</c> policy, the
    /// job runner as the listener's handler, and the GitHub bus from <c>bus</c>. Registrations a host adds afterwards win where the
    /// seam allows it (a <see cref="JobHandler"/>, a <see cref="JobGate"/>, an <see cref="ISecretStore"/> in tests).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The typed <c>node.json</c>.</param>
    /// <returns><paramref name="services"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    public static IServiceCollection AddNodeServices(this IServiceCollection services, NodeConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddSingleton(configuration);
        services.AddProcessRunner();
        services.AddSecretStore(configuration);
        services.AddEnvelopeSigning();
        services.AddSingleton(configuration.Poll.ToPollOptions());
        services.TryAddSingleton(new ContextEnvelopeOptions());
        // Deliverable 12: the laptop hooks' spool and the Stop hook; the hook resolves only with the secret patterns registered (fail closed).
        services.TryAddSingleton(sp => new ContextSpool(configuration.EffectiveNodeDir, configuration.Tenant, sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ContextEnvelopeOptions>()));
        services.TryAddSingleton(sp => new ContextStopHook(sp.GetRequiredService<ContextSpool>(), sp.GetRequiredService<SecretPatterns>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ContextEnvelopeOptions>()));
        // Deliverable 18: the session hooks' status writer; its publisher and the secret patterns are resolved only when a status is due.
        services.TryAddSingleton(sp => new WorkStatusHook(
            () => sp.GetRequiredService<WorkStatusPublisher>(),
            () => sp.GetRequiredService<SecretPatterns>(),
            sp.GetRequiredService<Git.GitClient>(),
            new WorkSessionStore(configuration.EffectiveNodeDir, configuration.Tenant),
            configuration.Supervision,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<ILoggerFactory>()?.CreateLogger<WorkStatusHook>() ?? NullLogger<WorkStatusHook>.Instance));
        // Deliverable 18: the asking side of a question; the sender is resolved only once a question is written (it needs the signing key and the secret patterns).
        services.TryAddSingleton(sp => new AskFlow(
            () => sp.GetRequiredService<QuestionAsker>(),
            sp.GetRequiredService<BusRepository>(),
            sp.GetRequiredService<BusInventory>(),
            new WorkSessionStore(configuration.EffectiveNodeDir, configuration.Tenant),
            new AskedStore(configuration.EffectiveNodeDir, configuration.Tenant),
            configuration.Supervision,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<ILoggerFactory>()?.CreateLogger<AskFlow>() ?? NullLogger<AskFlow>.Instance));
        if (configuration.SignWith is { } signWith)
        {
            services.AddSingleton<KeyId>(signWith);
        }

        services.AddClaudeCodeModelRunner(o =>
        {
            if (configuration.Claude.Path is { } path)
            {
                o.Path = path;
            }
        });
        services.AddNodeJsonPolicy();
        services.AddDiscovery();
        services.AddJobRunner();
        services.AddGitHubBus(o =>
        {
            o.Remote = BusRemoteFrom(configuration.Bus);
            o.Identity = new BusIdentity(configuration.Tenant, configuration.Machine);
            o.CheckoutRoot = configuration.Bus.LocalPath;
            o.Version = Bus.ServiceCollectionExtensions.InformationalVersion();
        });
        return services;
    }

    /// <summary>
    /// Registers the <see cref="SettingsInUse"/> that looks at the node's own <c>node.json</c> at <paramref name="configPath"/> (spec 55), in
    /// place of the one that only carries the values read at start. Only the Node host calls this; it runs <see cref="SettingsInUse.RunAsync"/>.
    /// Requires a registered <see cref="NodeConfiguration"/> read from that file.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configPath">The file the node read at start.</param>
    /// <returns><paramref name="services"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="configPath"/> is null or empty.</exception>
    public static IServiceCollection AddSettingsInUse(this IServiceCollection services, string configPath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(configPath);
        services.RemoveAll<SettingsInUse>();
        services.AddSingleton(sp => new SettingsInUse(
            sp.GetRequiredService<NodeConfiguration>(),
            configPath,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<ILoggerFactory>()?.CreateLogger<SettingsInUse>() ?? NullLogger<SettingsInUse>.Instance));
        return services;
    }

    /// <summary>The holder of the values read at start, unless the host registered the watching one (spec 55).</summary>
    internal static IServiceCollection TryAddSettingsInUse(this IServiceCollection services)
    {
        services.TryAddSingleton(sp => new SettingsInUse(sp.GetRequiredService<NodeConfiguration>()));
        return services;
    }

    /// <summary>The remote description from <c>node.json</c>: the configured URL as the HTTPS form when it is one (or a file URL in tests), as the SSH form when it is one; the provider fills in its host's defaults for the other.</summary>
    internal static BusRemote BusRemoteFrom(BusSettings bus)
    {
        if (Uri.TryCreate(bus.Remote, UriKind.Absolute, out Uri? url) && url.Scheme is "https" or "http" or "file")
        {
            return new BusRemote(bus.Owner, bus.Repo, bus.Branch, url, null);
        }

        // git@host:owner/repo.git or ssh://git@host/owner/repo.git
        Uri ssh = url is { Scheme: "ssh" } ? url : new Uri("ssh://" + bus.Remote.Replace(":", "/", StringComparison.Ordinal));
        return new BusRemote(bus.Owner, bus.Repo, bus.Branch, null, ssh);
    }
}
