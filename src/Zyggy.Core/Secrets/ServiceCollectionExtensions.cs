using System.Collections;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Zyggy.Core.Configuration;
using Zyggy.Core.Secrets.Windows;

namespace Zyggy.Core.Secrets;

/// <summary>Registers the secret stores of <c>Zyggy.Core</c> (founding spec §8).</summary>
public static partial class ServiceCollectionExtensions
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

    /// <summary>
    /// Registers the one <see cref="ISecretStore"/> a machine uses, selected by <c>node.json</c>'s <c>secrets.store</c> and the OS
    /// (spec 09 AC-3): <c>os</c> is the Windows Credential Manager on Windows and, on Linux, the systemd credentials directory
    /// then the 0600 file layer at <c>secrets.root</c>; <c>file</c> is the P0 file store everywhere, with one warning on Windows.
    /// Exactly one registration; a registration added afterwards wins (a test's substitute). Construction touches nothing.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The node's configuration.</param>
    /// <param name="environment">The process environment to read <c>CREDENTIALS_DIRECTORY</c> from; the real one when null.</param>
    /// <returns><paramref name="services"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> or <paramref name="configuration"/> is null.</exception>
    public static IServiceCollection AddSecretStore(this IServiceCollection services, NodeConfiguration configuration, IReadOnlyDictionary<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        IReadOnlyDictionary<string, string?> variables = environment ?? ProcessEnvironment();
        services.AddOptions<FileSecretStoreOptions>().Configure(o => o.RootDirectory = configuration.Secrets.Root);
        services.AddSingleton<ISecretStore>(sp =>
        {
            ILogger log = sp.GetService<ILoggerFactory>()?.CreateLogger("Zyggy.Core.Secrets") ?? NullLogger.Instance;
            IOptions<FileSecretStoreOptions> options = sp.GetRequiredService<IOptions<FileSecretStoreOptions>>();
            if (configuration.Secrets.Store == SecretStoreKind.File)
            {
                if (OperatingSystem.IsWindows())
                {
                    LogFileStoreOnWindows(log);
                }

                return new FileSecretStore(options);
            }

            if (OperatingSystem.IsWindowsVersionAtLeast(5, 1, 2600))
            {
                return WindowsCredentialManagerSecretStore.ForCurrentUser();
            }

            ILogger<LinuxSecretStore> linuxLog = sp.GetService<ILoggerFactory>()?.CreateLogger<LinuxSecretStore>() ?? NullLogger<LinuxSecretStore>.Instance;
            return new LinuxSecretStore(variables, new FileSecretStore(options), linuxLog);
        });
        return services;
    }

    // A snapshot of the process environment (plan 09 A7); the test passes its own dictionary.
    private static Dictionary<string, string?> ProcessEnvironment()
    {
        var variables = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            variables[(string)entry.Key] = entry.Value as string;
        }

        return variables;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "file secret store selected on Windows: spike and test use only; the product store is Credential Manager")]
    private static partial void LogFileStoreOnWindows(ILogger logger);
}
