using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Zyggy.Core.Processes;

/// <summary>Registers the process runner of <c>Zyggy.Core</c>, the single path to <c>git</c> and <c>claude</c> (founding spec §9).</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the real <see cref="IProcessRunner"/> as a singleton, and <see cref="TimeProvider.System"/> when no clock is registered.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns><paramref name="services"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> is null.</exception>
    public static IServiceCollection AddProcessRunner(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IProcessRunner, ProcessRunner>();
        return services;
    }
}
