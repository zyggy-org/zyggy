using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Jobs;
using Zyggy.Core.Models;
using Zyggy.Core.Policy;
using Zyggy.Core.Processes;
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
        services.AddFileSecretStore(o => o.RootDirectory = configuration.Secrets.Root);
        services.AddEnvelopeSigning();
        services.AddSingleton(configuration.Poll.ToPollOptions());
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
