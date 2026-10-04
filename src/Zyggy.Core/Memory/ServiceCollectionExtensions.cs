using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Zyggy.Core.Memory;

/// <summary>Registers the memory digest of <c>Zyggy.Core</c> (founding spec §7 Context loading).</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <paramref name="paths"/>, <paramref name="options"/> and a singleton <see cref="DigestBuilder"/>, and
    /// <see cref="TimeProvider.System"/> when no clock is registered.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="paths">The principal's memory paths.</param>
    /// <param name="options">The section caps.</param>
    /// <returns><paramref name="services"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    public static IServiceCollection AddMemoryDigest(this IServiceCollection services, MemoryPaths paths, DigestOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(paths);
        services.AddSingleton(options);
        services.AddSingleton<DigestBuilder>();
        return services;
    }
}
