using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Git;
using Zyggy.Core.Memory;
using Zyggy.Core.Policy;
using Zyggy.Core.Processes;

namespace Zyggy.Core.Registry;

/// <summary>Registers discovery (spec 14): the scanner and its options from <c>node.json</c>.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="DiscoveryOptions"/> (filled from a registered <see cref="NodeConfiguration"/>'s <c>discovery</c> section) and the
    /// <see cref="DiscoveryScanner"/>. The scanner resolves the secret patterns lazily, so an <c>AddSecretPatterns</c> made later is seen; without
    /// them a full scan runs as names only.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> is null.</exception>
    public static IServiceCollection AddDiscovery(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<DiscoveryOptions>()
            .Configure<IServiceProvider>((options, sp) =>
            {
                if (sp.GetService<NodeConfiguration>() is { } configuration)
                {
                    options.IntervalMinutes = configuration.Discovery.IntervalMinutes;
                    options.LastSeenHours = configuration.Discovery.LastSeenHours;
                    options.Detail = configuration.Discovery.Detail;
                }
            })
            .Validate(options => options.Validate() is null, "discovery: intervalMinutes 1-1440, lastSeenHours 1-168");
        services.TryAddSingleton(sp =>
        {
            TimeProvider clock = sp.GetRequiredService<TimeProvider>();
            return new DiscoveryScanner(
                new GitClient(sp.GetRequiredService<IProcessRunner>(), new GitClientOptions(), clock),
                sp.GetService<SecretPatterns>(),
                sp.GetRequiredService<IOptions<DiscoveryOptions>>().Value,
                clock,
                sp.GetService<ILogger<DiscoveryScanner>>() ?? NullLogger<DiscoveryScanner>.Instance);
        });

        // The publisher writes through the host's one BusRepository (registered by the bus provider); resolved only by hosts that have one.
        services.TryAddSingleton(sp => new RegistryPublisher(
            sp.GetRequiredService<BusRepository>(),
            sp.GetRequiredService<IOptions<DiscoveryOptions>>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<ILogger<RegistryPublisher>>() ?? NullLogger<RegistryPublisher>.Instance));

        // The node's own latest scan (A14-3) and the cycle the Node's timer schedules (plan 14 A3).
        services.TryAddSingleton<RegistrySnapshot>();
        services.TryAddSingleton(sp => new DiscoveryLoop(
            sp.GetRequiredService<DiscoveryScanner>(),
            sp.GetRequiredService<RegistrySnapshot>(),
            sp.GetRequiredService<RegistryPublisher>(),
            sp.GetRequiredService<IPolicySource>(),
            sp.GetRequiredService<NodeConfiguration>(),
            sp.GetRequiredService<IOptions<DiscoveryOptions>>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<ILogger<DiscoveryLoop>>() ?? NullLogger<DiscoveryLoop>.Instance));
        return services;
    }
}
