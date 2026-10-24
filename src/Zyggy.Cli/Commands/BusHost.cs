using System.CommandLine;
using System.Reflection;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;

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

    /// <summary>Prints a configuration error line and returns exit code 3.</summary>
    public static int ConfigurationError(string message)
    {
        Console.Error.WriteLine("zyggy: configuration error: " + message);
        return ExitCodes.Configuration;
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

    public static string InformationalVersion() =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
}
