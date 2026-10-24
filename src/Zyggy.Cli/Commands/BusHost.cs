using System.CommandLine;
using System.Reflection;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Verbs;

namespace Zyggy.Cli.Commands;

/// <summary>
/// What every bus verb shares (spec 04 CLI surface): the <c>--config</c>/<c>--verbose</c> options, loading <c>node.json</c>
/// into exit code 3 with one stderr line, and the production composition root — process runner, file secret store, envelope
/// signer and the GitHub bus — logging to stderr.
/// </summary>
internal static class BusHost
{
    /// <summary>The test hook that points the API request at a loopback stand-in (spec 04 Assumption A3); never a credential.</summary>
    private const string ApiBaseVariable = "ZYGGY_BUS_API_BASE";

    public static Option<string?> ConfigOption { get; } = new("--config") { Description = "Path of node.json (default: ZYGGY_NODE_JSON, then the per-user default)." };

    public static Option<bool> VerboseOption { get; } = new("--verbose") { Description = "Debug logging on stderr." };

    /// <summary>Loads <c>node.json</c>; on refusal prints <c>zyggy: configuration error: &lt;key&gt;: &lt;detail&gt;</c> and returns exit code 3.</summary>
    public static (NodeConfiguration? Configuration, int ExitCode) LoadConfiguration(ParseResult parseResult, CliEnvironment environment)
    {
        string path = NodeConfigurationReader.ResolvePath(parseResult.GetValue(ConfigOption), environment.Variables);
        NodeConfigurationResult result = NodeConfigurationReader.Read(path);
        if (result.IsOk)
        {
            return (result.Configuration, ExitCodes.Ok);
        }

        return (null, ConfigurationError(result.ToString()));
    }

    /// <summary>Loads <c>node.json</c> for a verb with an injected console (deliverable 08): the same rule, the error line on <paramref name="io"/>.</summary>
    public static (NodeConfiguration? Configuration, int ExitCode) LoadConfiguration(ParseResult parseResult, CliEnvironment environment, VerbIo io)
    {
        string path = NodeConfigurationReader.ResolvePath(parseResult.GetValue(ConfigOption), environment.Variables);
        NodeConfigurationResult result = NodeConfigurationReader.Read(path);
        return result.IsOk ? (result.Configuration, ExitCodes.Ok) : (null, ConfigurationError(io, result.ToString()));
    }

    /// <summary>Prints a configuration error line and returns exit code 3.</summary>
    public static int ConfigurationError(string message)
    {
        Console.Error.WriteLine("zyggy: configuration error: " + message);
        return ExitCodes.Configuration;
    }

    /// <summary>Prints a configuration error line on <paramref name="io"/> and returns exit code 3.</summary>
    public static int ConfigurationError(VerbIo io, string message)
    {
        io.Error.WriteLine("zyggy: configuration error: " + message);
        return ExitCodes.Configuration;
    }

    /// <summary>
    /// The configuration-shaped checks every bus verb runs before touching anything (spec 04 AC-27; spec 08): <c>bus.localPath</c> is a git
    /// repository, on <c>bus.branch</c>, holding <c>tenants/&lt;tenant&gt;/</c>. Each refusal is exit 3 with one line; nothing is stashed or pulled.
    /// </summary>
    public static async Task<int> CheckCheckoutAsync(ServiceProvider services, NodeConfiguration configuration, VerbIo io, CancellationToken ct)
    {
        string localPath = configuration.Bus.LocalPath;
        if (!Directory.Exists(localPath) || !Directory.Exists(Path.Join(localPath, ".git")))
        {
            return ConfigurationError(io, "bus.localPath is not a git repository");
        }

        string? branch = await services.GetRequiredService<BusRepository>().CheckoutBranchAsync(ct);
        if (branch is null)
        {
            return ConfigurationError(io, "bus.localPath is not a git repository");
        }

        if (!string.Equals(branch, configuration.Bus.Branch, StringComparison.Ordinal))
        {
            return ConfigurationError(io, $"checkout is on '{branch}', expected '{configuration.Bus.Branch}'");
        }

        if (!Directory.Exists(BusPaths.Resolve(localPath, BusPaths.Tenant(configuration.Tenant))))
        {
            return ConfigurationError(io, $"no {BusPaths.Tenant(configuration.Tenant)}/ directory in bus.localPath");
        }

        return ExitCodes.Ok;
    }

    /// <summary>The production services for <paramref name="configuration"/>: Core's one composition root (spec 07 ex-Q8) plus the CLI's stderr logging and its API test hook; <paramref name="extra"/> is applied last and wins.</summary>
    public static ServiceProvider BuildServices(NodeConfiguration configuration, bool verbose, CliEnvironment environment, Action<IServiceCollection>? extra = null)
    {
        var services = new ServiceCollection()
            .AddLogging(logging => logging
                .AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace)
                .SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information))
            .AddNodeServices(configuration);
        string? apiBase = environment.Get(ApiBaseVariable);
        services.Configure<GitHubBusOptions>(o => o.ApiBaseUri = apiBase is null ? null : new Uri(apiBase));
        extra?.Invoke(services);
        return services.BuildServiceProvider();
    }

    /// <summary>The CLI assembly's informational version without build metadata — the same in the published single file, deterministic in process (plan 08 A5).</summary>
    public static string InformationalVersion() =>
        typeof(BusHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
}
