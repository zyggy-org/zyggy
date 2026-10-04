using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

using Zyggy.Core.Git;
using Zyggy.Core.Models;
using Zyggy.Core.Processes;

namespace Zyggy.Core.Dream;

/// <summary>Registers the dream pass of <c>Zyggy.Core</c> (spec 28).</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="DreamRunner"/> and everything it uses for <paramref name="environment"/> and <paramref name="options"/>,
    /// plus the process runner and the Claude Code model runner. Logging must be registered by the host.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="environment">Where and for whom the dream runs.</param>
    /// <param name="options">The thresholds.</param>
    /// <param name="configureModel">Optional model-runner configuration, for example the <c>claude</c> path.</param>
    /// <returns><paramref name="services"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    public static IServiceCollection AddZyggyDream(
        this IServiceCollection services,
        DreamEnvironment environment,
        DreamOptions options,
        Action<ClaudeCodeOptions>? configureModel = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(options);
        services.AddProcessRunner();
        services.AddClaudeCodeModelRunner(configureModel);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(environment);
        services.AddSingleton(options);
        services.AddSingleton<DreamPrompts>();
        services.AddSingleton(sp => new DreamFiler(sp.GetRequiredService<IModelRunner>(), sp.GetRequiredService<DreamPrompts>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new Compressor(sp.GetRequiredService<IModelRunner>(), sp.GetRequiredService<DreamPrompts>()));
        services.AddSingleton(sp => new GitClient(sp.GetRequiredService<IProcessRunner>(), new GitClientOptions(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new DreamRunner(
            environment,
            options,
            sp.GetRequiredService<DreamFiler>(),
            sp.GetRequiredService<Compressor>(),
            sp.GetRequiredService<GitClient>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<DreamRunner>>(),
            new Migrator(sp.GetRequiredService<IModelRunner>(), sp.GetRequiredService<DreamPrompts>())));
        return services;
    }
}
