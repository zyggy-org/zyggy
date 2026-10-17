using System.Collections;
using System.Reflection;

using Serilog;
using Serilog.Events;

using Zyggy.Core.Configuration;

namespace Zyggy.Node;

/// <summary>
/// The <c>zyggy-node</c> entry point (spec 07 Contracts → Hosting): parses <c>--version</c>/<c>--config</c>, reads <c>node.json</c>
/// (exit 3 on refusal, <c>signWith</c> mandatory), creates the log directory under the node directory (exit 6 when it cannot),
/// and builds the generic host: Serilog (daily rolling file + console, <c>tenant</c>/<c>machine</c> enriched), the Windows-service
/// and systemd lifetimes (no-ops outside a service manager), the host options, and the hosted services. Holds no state of its own.
/// </summary>
public static class NodeHost
{
    private const string ConfigOption = "--config";

    private const string VersionOption = "--version";

    private const string OutputTemplate = "{utc} [{Level:u3}] {tenant} {machine} {phase} {jobId} {SourceContext}: {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// Runs the node to completion: <c>--version</c> prints the informational version and returns 0 without reading <c>node.json</c>;
    /// otherwise the host built by <see cref="Build"/> runs until <paramref name="cancellationToken"/> or the loop stops it, and the
    /// exit code is the one the loop decided (0 when it decided none). A <see cref="NodeStartupException"/> becomes one stderr line and its code.
    /// </summary>
    /// <param name="args">The command line.</param>
    /// <param name="cancellationToken">Stops the host (the process-level SIGINT/SIGTERM handling is the generic host's own).</param>
    /// <param name="configure">Test hook (plan 07 A8): applied to the services after the production registrations.</param>
    /// <returns>The process exit code (<see cref="NodeExitCodes"/>).</returns>
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken, Action<IServiceCollection>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Contains(VersionOption, StringComparer.Ordinal))
        {
            Console.Out.WriteLine(InformationalVersion());
            return NodeExitCodes.Ok;
        }

        IHost host;
        try
        {
            host = Build(args, configure);
        }
        catch (NodeStartupException ex)
        {
            Console.Error.WriteLine(ex.ExitCode == NodeExitCodes.Configuration ? "zyggy-node: configuration error: " + ex.Message : "zyggy-node: " + ex.Message);
            return ex.ExitCode;
        }

        // RunAsync disposes the host when it returns; the exit state is a singleton resolved before that.
        NodeExitState exitState = host.Services.GetRequiredService<NodeExitState>();
        try
        {
            await host.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancelled before the host had started: a normal stop.
        }

        return exitState.Code;
    }

    /// <summary>
    /// Builds the host for the <c>node.json</c> named by <c>--config</c>, then <c>ZYGGY_NODE_JSON</c>, then the per-user default.
    /// Nothing of the command line reaches <c>IConfiguration</c>; <c>node.json</c> is the single configuration file (§10).
    /// </summary>
    /// <param name="args">The command line.</param>
    /// <param name="configure">Test hook (plan 07 A8): applied to the services after the production registrations.</param>
    /// <returns>The built, not yet started host.</returns>
    /// <exception cref="NodeStartupException">A refused <c>node.json</c>, a missing <c>signWith</c> or an unknown argument (exit 3); a log directory that cannot be created (exit 6).</exception>
    public static IHost Build(string[] args, Action<IServiceCollection>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? configOption = ParseConfigOption(args);
        string path = NodeConfigurationReader.ResolvePath(configOption, ProcessEnvironment());
        NodeConfigurationResult result = NodeConfigurationReader.Read(path);
        if (!result.IsOk)
        {
            throw new NodeStartupException(NodeExitCodes.Configuration, result.ToString());
        }

        NodeConfiguration configuration = result.Configuration!;
        if (configuration.SignWith is null)
        {
            throw new NodeStartupException(NodeExitCodes.Configuration, "signWith missing: the node must sign its reports");
        }

        string logDirectory = LogDirectory(configuration.EffectiveNodeDir);
        try
        {
            Directory.CreateDirectory(logDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new NodeStartupException(NodeExitCodes.Startup, $"cannot create the log directory {logDirectory}: {ex.Message}");
        }

        var options = new NodeHostOptions();
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], DisableDefaults = true });
        builder.Logging.ClearProviders();
        builder.Services.AddSerilog(CreateLogger(configuration, logDirectory), dispose: true);
        builder.Services.AddWindowsService();
        builder.Services.AddSystemd();
        builder.Services.Configure<HostOptions>(o =>
        {
            o.ShutdownTimeout = options.ShutdownTimeout;
            o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost;
        });
        builder.Services.AddOptions<NodeHostOptions>();
        builder.Services.AddSingleton(configuration);
        builder.Services.AddSingleton<NodeExitState>();
        builder.Services.AddHostedService<StartupBanner>();
        configure?.Invoke(builder.Services);
        return builder.Build();
    }

    /// <summary>The informational version of this binary without build metadata (<c>1.2.3-alpha.4</c>, never the <c>+sha</c> part).</summary>
    /// <returns>The version text.</returns>
    public static string InformationalVersion() =>
        typeof(NodeHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    /// <summary>The one place the log directory is derived from the node directory (§11: <c>&lt;nodeDir&gt;/logs/</c>).</summary>
    internal static string LogDirectory(string nodeDir) => Path.GetFullPath(Path.Join(nodeDir, "logs"));

    private static Serilog.Core.Logger CreateLogger(NodeConfiguration configuration, string logDirectory) =>
        new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .Enrich.With<UtcTimestampEnricher>()
            .Enrich.WithProperty("tenant", configuration.Tenant.Value)
            .Enrich.WithProperty("machine", configuration.Machine.Value)
            .WriteTo.File(
                Path.Join(logDirectory, "zyggy-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: false,
                outputTemplate: OutputTemplate)
            .WriteTo.Console(outputTemplate: OutputTemplate)
            .CreateLogger();

    private static string? ParseConfigOption(string[] args)
    {
        string? value = null;
        for (var i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (string.Equals(arg, ConfigOption, StringComparison.Ordinal))
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new NodeStartupException(NodeExitCodes.Configuration, "--config: path missing");
                }

                value = args[++i];
            }
            else if (arg.StartsWith(ConfigOption + "=", StringComparison.Ordinal))
            {
                value = arg[(ConfigOption.Length + 1)..];
            }
            else
            {
                throw new NodeStartupException(NodeExitCodes.Configuration, "unknown argument: " + arg);
            }
        }

        return value;
    }

    private static Dictionary<string, string?> ProcessEnvironment()
    {
        var variables = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            variables[(string)entry.Key] = entry.Value as string;
        }

        return variables;
    }
}
