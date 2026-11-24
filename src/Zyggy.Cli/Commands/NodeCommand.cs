using System.CommandLine;

using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Health;
using Zyggy.Core.Processes;
using Zyggy.Core.Verbs;

namespace Zyggy.Cli.Commands;

/// <summary>
/// <c>zyggy node stop</c> (spec 61 §4): ends the listener that holds this machine's bus checkout — only this user's <c>zyggy-node</c>
/// named by the owner record beside the listen lock, with the recorded start time, and only when the node answering on the health URL (if
/// any) is that same process for this tenant and machine. Exit codes: 0 stopped or no listener running, 3 configuration, 5 the recorded pid
/// or the health answer is not this listener (nothing touched; also inside a job, which must not stop the node running it), 6 the
/// process could not be ended. Reads the owner record, not the scheduled task or the service. Rendering only; the rules are Core's
/// (<see cref="NodeStopper"/>).
/// </summary>
internal static class NodeCommand
{
    public static Command Create(CliEnvironment environment, VerbIo io, Action<IServiceCollection>? configure)
    {
        var stop = new Command("stop", "End this machine's zyggy-node that holds the bus checkout (its runs are interrupted; their claims resume at the next start).")
        {
            BusHost.ConfigOption, BusHost.VerboseOption,
        };
        stop.SetAction((parseResult, ct) => StopAsync(environment, io, configure, parseResult, ct));
        return new Command("node", "This machine's node service.") { stop };
    }

    private static async Task<int> StopAsync(CliEnvironment environment, VerbIo io, Action<IServiceCollection>? configure, ParseResult parseResult, CancellationToken ct)
    {
        (NodeConfiguration? configuration, int exitCode) = BusHost.LoadConfiguration(parseResult, environment, io);
        if (configuration is null)
        {
            return exitCode;
        }

        // A job runs as a child of the node: ending the node's tree would end the job before its report (CLAUDE.md, job runs).
        if (!string.IsNullOrEmpty(environment.Get("ZYGGY_JOB")))
        {
            await io.Out.WriteLineAsync("a job cannot stop the node that runs it; nothing was touched");
            return ExitCodes.Refused;
        }

        Uri url;
        try
        {
            url = environment.Get(BusHost.HealthUrlVariable) is { } text ? new Uri(text) : HealthClient.DefaultUrl;
        }
        catch (UriFormatException)
        {
            return BusHost.ConfigurationError(io, "health url must be loopback");
        }

        await using ServiceProvider services = BusHost.BuildServices(configuration, parseResult.GetValue(BusHost.VerboseOption), environment, configure);
        TimeProvider clock = services.GetRequiredService<TimeProvider>();
        HealthProbe health;
        try
        {
            using var client = new HealthClient(url, clock);
            health = await client.ReadAsync(ct);
        }
        catch (ArgumentException)
        {
            return BusHost.ConfigurationError(io, "health url must be loopback");
        }

        var stopper = new NodeStopper(configuration.Bus.LocalPath, services.GetRequiredService<IProcessTable>(), clock);
        NodeStopOutcome outcome = await stopper.StopAsync(health, configuration.Tenant, configuration.Machine, url, ct);
        await io.Out.WriteLineAsync(outcome.Line);
        return outcome.Result switch
        {
            NodeStopResult.Stopped or NodeStopResult.NotRunning => ExitCodes.Ok,
            NodeStopResult.Foreign => ExitCodes.Refused,
            _ => ExitCodes.Failed,
        };
    }
}
