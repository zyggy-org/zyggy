using System.CommandLine;
using System.Reflection;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Envelope;
using Zyggy.Core.Processes;
using Zyggy.Core.Secrets;

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

    /// <summary>The production services for <paramref name="configuration"/>.</summary>
    public static ServiceProvider BuildServices(NodeConfiguration configuration, bool verbose, CliEnvironment environment, Action<IServiceCollection>? extra = null)
    {
        var services = new ServiceCollection()
            .AddLogging(logging => logging
                .AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace)
                .SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information))
            .AddProcessRunner()
            .AddFileSecretStore(o => o.RootDirectory = configuration.Secrets.Root)
            .AddEnvelopeSigning()
            .AddSingleton(configuration.Poll.ToPollOptions());
        if (configuration.SignWith is { } signWith)
        {
            services.AddSingleton<KeyId>(signWith);
        }

        string? apiBase = environment.Get(ApiBaseVariable);
        services.AddGitHubBus(o =>
        {
            o.Remote = BusRemoteFrom(configuration.Bus);
            o.Identity = new BusIdentity(configuration.Tenant, configuration.Machine);
            o.CheckoutRoot = configuration.Bus.LocalPath;
            o.Version = InformationalVersion();
            o.ApiBaseUri = apiBase is null ? null : new Uri(apiBase);
        });
        extra?.Invoke(services);
        return services.BuildServiceProvider();
    }

    /// <summary>The remote description from <c>node.json</c>: the configured URL as the HTTPS form when it is one (or a file URL in tests), as the SSH form when it is one; the provider fills in its host's defaults for the other.</summary>
    private static BusRemote BusRemoteFrom(BusSettings bus)
    {
        if (Uri.TryCreate(bus.Remote, UriKind.Absolute, out Uri? url) && url.Scheme is "https" or "http" or "file")
        {
            return new BusRemote(bus.Owner, bus.Repo, bus.Branch, url, null);
        }

        // git@host:owner/repo.git or ssh://git@host/owner/repo.git
        Uri ssh = url is { Scheme: "ssh" } ? url : new Uri("ssh://" + bus.Remote.Replace(":", "/", StringComparison.Ordinal));
        return new BusRemote(bus.Owner, bus.Repo, bus.Branch, null, ssh);
    }

    public static string InformationalVersion() =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
}
