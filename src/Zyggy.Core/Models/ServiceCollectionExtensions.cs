using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Zyggy.Core.Models;

/// <summary>Registers the model runner of <c>Zyggy.Core</c> (founding spec §9 seam <see cref="IModelRunner"/>).</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Claude Code CLI runner as the singleton <see cref="IModelRunner"/>. Requires an
    /// <see cref="Processes.IProcessRunner"/>; registers <see cref="TimeProvider.System"/> when no clock is registered.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional configuration, for example the <c>claude</c> path.</param>
    /// <returns><paramref name="services"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> is null.</exception>
    public static IServiceCollection AddClaudeCodeModelRunner(this IServiceCollection services, Action<ClaudeCodeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<ClaudeCodeOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IModelRunner, ClaudeCodeCliRunner>();
        return services;
    }
}
