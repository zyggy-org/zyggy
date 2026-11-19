using System.CommandLine;

using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Configuration;
using Zyggy.Core.Registry;
using Zyggy.Core.Verbs;

namespace Zyggy.Cli.Commands;

/// <summary>
/// <c>zyggy settings show | set | add | remove</c> (spec 55): the five settings a running node applies, changed on Central through the binary
/// only. Argument parsing, checks and the write are Core's <see cref="SettingsVerb"/>; this class gives it the console, the clock of the
/// production services and a scan with the real <see cref="DiscoveryScanner"/> built for the settings under test.
/// </summary>
internal static class SettingsCommand
{
    /// <summary>The <c>--help</c> entry; <see cref="RunAsync"/> runs the verb with its own parser.</summary>
    public static Command Create() =>
        new("settings", "Show the five settings a running node applies; on Central, in an attended session, change them (zyggy settings show | set | add | remove <key> <value>).");

    /// <summary>Runs the verb with the arguments after <c>settings</c>.</summary>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, CliEnvironment environment, VerbIo io, Action<IServiceCollection>? configure, CancellationToken ct)
    {
        TimeProvider clock = TimeProvider.System;
        if (configure is not null)
        {
            var probe = new ServiceCollection();
            configure(probe);
            await using ServiceProvider configured = probe.BuildServiceProvider();
            clock = configured.GetService<TimeProvider>() ?? clock;
        }

        var verb = new SettingsVerb(environment.Variables, clock, (configuration, token) => ScanAsync(configuration, environment, configure, token));
        return await verb.RunAsync(args, io, ct);
    }

    /// <summary>
    /// The projects a discovery scan with <paramref name="configuration"/> finds: the node's scanner, built from the production services for
    /// exactly these settings (names and folders only; nothing inside a project is read, nothing is published).
    /// </summary>
    private static async Task<IReadOnlyList<SettingsProject>> ScanAsync(
        NodeConfiguration configuration,
        CliEnvironment environment,
        Action<IServiceCollection>? configure,
        CancellationToken ct)
    {
        await using ServiceProvider services = BusHost.BuildServices(configuration, verbose: false, environment, configure);
        return services.GetRequiredService<DiscoveryScanner>().FindProjects(configuration.DevRoots, ct);
    }
}
