using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Configuration;

namespace Zyggy.Core.Policy;

/// <summary>Registers the policy source of <c>Zyggy.Core</c> (founding spec §9 seam <see cref="IPolicySource"/>).</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the singleton <see cref="IPolicySource"/> read from <c>node.json</c>. Requires a registered
    /// <see cref="NodeConfiguration"/> (the host registers what it read); without one, resolving throws — there is no hidden default.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns><paramref name="services"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> is null.</exception>
    public static IServiceCollection AddNodeJsonPolicy(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IPolicySource>(sp => new NodeJsonPolicySource(sp.GetRequiredService<NodeConfiguration>()));
        return services;
    }
}
