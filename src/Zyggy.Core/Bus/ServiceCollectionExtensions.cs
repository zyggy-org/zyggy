using System.Net;
using System.Reflection;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Zyggy.Core.Envelope;
using Zyggy.Core.Git;
using Zyggy.Core.Processes;
using Zyggy.Core.Secrets;

namespace Zyggy.Core.Bus;

/// <summary>Registers the bus layer of <c>Zyggy.Core</c> (founding spec §5, §9).</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the GitHub implementation of <see cref="IBusProvider"/>, the git client with this machine's commit identity
    /// (<c>zyggy (&lt;machine&gt;) &lt;machine&gt;@zyggy.org</c>, §5) and the <see cref="BusRepository"/> as singletons. Requires
    /// <see cref="ISecretStore"/>, <see cref="IProcessRunner"/>, <see cref="TimeProvider"/> and the envelope signer to be
    /// registered, and <see cref="GitHubBusOptions.Remote"/>, <see cref="GitHubBusOptions.Identity"/> and
    /// <see cref="GitHubBusOptions.CheckoutRoot"/> to be set — there is no hidden default.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">The options; normally filled from <c>node.json</c>.</param>
    /// <returns><paramref name="services"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> is null.</exception>
    public static IServiceCollection AddGitHubBus(this IServiceCollection services, Action<GitHubBusOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<GitHubBusOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.TryAddSingleton(sp =>
        {
            GitHubBusOptions options = sp.GetRequiredService<IOptions<GitHubBusOptions>>().Value;
            BusIdentity identity = options.Identity ?? throw Missing(nameof(GitHubBusOptions.Identity));
            var gitOptions = new GitClientOptions
            {
                Identity = new GitIdentity("zyggy (" + identity.Machine.Value + ")", identity.Machine.Value + "@zyggy.org"),
                Environment = options.GitEnvironment ?? new Dictionary<string, string?>(),
            };
            return new GitClient(sp.GetRequiredService<IProcessRunner>(), gitOptions, sp.GetRequiredService<TimeProvider>());
        });
        services.TryAddSingleton(sp =>
        {
            GitHubBusOptions options = sp.GetRequiredService<IOptions<GitHubBusOptions>>().Value;
            BusIdentity identity = options.Identity ?? throw Missing(nameof(GitHubBusOptions.Identity));
            string checkoutRoot = options.CheckoutRoot ?? throw Missing(nameof(GitHubBusOptions.CheckoutRoot));
            ILogger<BusRepository> log = sp.GetService<ILoggerFactory>()?.CreateLogger<BusRepository>() ?? NullLogger<BusRepository>.Instance;
            return new BusRepository(
                sp.GetRequiredService<GitClient>(),
                sp.GetRequiredService<IBusProvider>(),
                identity,
                checkoutRoot,
                sp.GetRequiredService<EnvelopeSigner>(),
                sp.GetRequiredService<TimeProvider>(),
                options.Jitter ?? Random.Shared.NextDouble,
                log);
        });
        services.TryAddSingleton<IBusProvider>(sp =>
        {
            GitHubBusOptions options = sp.GetRequiredService<IOptions<GitHubBusOptions>>().Value;
            BusRemote remote = options.Remote ?? throw Missing(nameof(GitHubBusOptions.Remote));
            BusIdentity identity = options.Identity ?? throw Missing(nameof(GitHubBusOptions.Identity));
            string checkoutRoot = options.CheckoutRoot ?? throw Missing(nameof(GitHubBusOptions.CheckoutRoot));
            ILogger<GitHubBusProvider> log = sp.GetService<ILoggerFactory>()?.CreateLogger<GitHubBusProvider>() ?? NullLogger<GitHubBusProvider>.Instance;
            return new GitHubBusProvider(
                options.HandlerFactory?.Invoke() ?? CreateHandler(),
                sp.GetRequiredService<GitClient>(),
                remote,
                sp.GetRequiredService<ISecretStore>(),
                identity,
                checkoutRoot,
                options.Version ?? InformationalVersion(),
                options.ApiBaseUri,
                sp.GetRequiredService<TimeProvider>(),
                log);
        });
        return services;
    }

    /// <summary>
    /// The one place an HTTP handler for the bus is created (§5 item 7): the system proxy with the user's default credentials
    /// (the corporate proxy on the work laptop), no automatic redirects. The product's other HTTP clients bypass the proxy; the bus must not.
    /// </summary>
    internal static SocketsHttpHandler CreateHandler() => new()
    {
        UseProxy = true,
        DefaultProxyCredentials = CredentialCache.DefaultCredentials,
        AllowAutoRedirect = false,
    };

    private static InvalidOperationException Missing(string option) =>
        new($"GitHubBusOptions.{option} is not set: the bus has no default remote, identity or checkout (configure it from node.json).");

    private static string InformationalVersion() =>
        (Assembly.GetEntryAssembly() ?? typeof(ServiceCollectionExtensions).Assembly)
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
}
