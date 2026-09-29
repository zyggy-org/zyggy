using Microsoft.Extensions.DependencyInjection;

namespace Zyggy.Core.Secrets;

/// <summary>Registers the secret stores of <c>Zyggy.Core</c> (founding spec §8).</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the file secret store as the singleton <see cref="ISecretStore"/>: keys under
    /// <c>&lt;root&gt;/&lt;tenant&gt;/&lt;name&gt;</c> with owner-only permissions (the P0 store, O20).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional configuration, for example the root directory.</param>
    /// <returns><paramref name="services"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> is null.</exception>
    public static IServiceCollection AddFileSecretStore(this IServiceCollection services, Action<FileSecretStoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<FileSecretStoreOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.AddSingleton<ISecretStore, FileSecretStore>();
        return services;
    }
}
