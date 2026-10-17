using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Git;
using Zyggy.Core.Models;
using Zyggy.Core.Policy;
using Zyggy.Core.Processes;

namespace Zyggy.Core.Jobs;

/// <summary>Registers the job runner of <c>Zyggy.Core</c> (founding spec §6) as the bus listener's <see cref="JobHandler"/>.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the singleton <see cref="JobRunner"/> and its <see cref="JobRunner.RunAsync"/> as the <see cref="JobHandler"/>.
    /// Requires an <see cref="IModelRunner"/>, an <see cref="IPolicySource"/> and an <see cref="IProcessRunner"/> — resolving throws
    /// without them, there is no hidden default; registers <see cref="TimeProvider.System"/> when no clock is registered. The runner
    /// gets its own identity-less git client (for <c>rev-parse</c> only), so this and <c>AddGitHubBus</c> may be called in either order.
    /// When <see cref="JobRunnerOptions.NodeDir"/> is left unset, a registered <see cref="NodeConfiguration"/>'s <c>nodeDir</c> fills it.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional configuration; production leaves the test-only options empty.</param>
    /// <returns><paramref name="services"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> is null.</exception>
    public static IServiceCollection AddJobRunner(this IServiceCollection services, Action<JobRunnerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = services.AddOptions<JobRunnerOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        options.PostConfigure<IServiceProvider>((o, sp) => o.NodeDir ??= sp.GetService<NodeConfiguration>()?.NodeDir);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(sp =>
        {
            var clock = sp.GetRequiredService<TimeProvider>();
            return new JobRunner(
                sp.GetRequiredService<IModelRunner>(),
                sp.GetRequiredService<IPolicySource>(),
                new GitClient(sp.GetRequiredService<IProcessRunner>(), new GitClientOptions(), clock),
                sp.GetRequiredService<IOptions<JobRunnerOptions>>(),
                clock,
                sp.GetService<ILoggerFactory>()?.CreateLogger<JobRunner>() ?? NullLogger<JobRunner>.Instance);
        });
        services.AddSingleton<JobHandler>(sp => sp.GetRequiredService<JobRunner>().RunAsync);
        return services;
    }
}
