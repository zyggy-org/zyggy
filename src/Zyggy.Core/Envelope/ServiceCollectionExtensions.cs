using Microsoft.Extensions.DependencyInjection;

namespace Zyggy.Core.Envelope;

/// <summary>Registers envelope signing (founding spec §4 "Signature").</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="EnvelopeSigner"/> as a singleton. It requires an <c>ISecretStore</c> registration
    /// (<c>AddFileSecretStore</c> in P0; the OS stores from deliverable 09); none is registered here, so a missing store
    /// fails at resolution instead of silently using a default.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns><paramref name="services"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> is null.</exception>
    public static IServiceCollection AddEnvelopeSigning(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<EnvelopeSigner>();
        return services;
    }
}
