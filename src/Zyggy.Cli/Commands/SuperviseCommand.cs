using System.CommandLine;

using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Supervision;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Verbs;

namespace Zyggy.Cli.Commands;

/// <summary>
/// <c>zyggy supervise stop | start</c> (deliverable 18, "Stop"): <c>stop</c> writes the owner's marker in the node folder first — so it
/// holds without the bus — and on Central then revokes every live mandate; it is a kill switch and also runs unattended. <c>start</c>
/// removes the marker and runs only in an attended session. Ordering, option reading and rendering only; the marker is
/// <see cref="SupervisionSwitch"/>, the revocation <see cref="MandateGranter.RevokeAsync"/>.
/// </summary>
internal static class SuperviseCommand
{
    public static Command Create(CliEnvironment environment, VerbIo io, Action<IServiceCollection>? configure)
    {
        var stop = new Command("stop", "Stop supervised work: no mandate job is accepted on this machine; on Central every mandate is revoked and no supervisor run starts.")
        {
            BusHost.ConfigOption, BusHost.VerboseOption,
        };
        stop.SetAction((parseResult, ct) => StopAsync(environment, io, configure, parseResult, ct));
        var start = new Command("start", "Attended only: lift the stop on this machine; mandates revoked by the stop stay revoked.")
        {
            BusHost.ConfigOption, BusHost.VerboseOption,
        };
        start.SetAction((parseResult, ct) => StartAsync(environment, io, parseResult));
        return new Command("supervise", "Stop or start supervised work on this machine.") { stop, start };
    }

    private static async Task<int> StopAsync(CliEnvironment environment, VerbIo io, Action<IServiceCollection>? configure, ParseResult parseResult, CancellationToken ct)
    {
        (NodeConfiguration? configuration, int exitCode) = BusHost.LoadConfiguration(parseResult, environment, io);
        if (configuration is null)
        {
            return exitCode;
        }

        try
        {
            SupervisionSwitch.Stop(configuration.EffectiveNodeDir, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return BusHost.ConfigurationError(io, "supervise: cannot write the stop marker in " + configuration.EffectiveNodeDir + ": " + ex.Message);
        }

        await io.Out.WriteLineAsync("stopped: no supervised work starts on " + configuration.Machine.Value + " until zyggy supervise start");
        if (configuration.Machine != MachineName.Central)
        {
            return ExitCodes.Ok;
        }

        await using ServiceProvider services = BusHost.BuildServices(configuration, parseResult.GetValue(BusHost.VerboseOption), environment, configure);
        int checkout = await BusHost.CheckCheckoutAsync(services, configuration, io, ct);
        if (checkout != ExitCodes.Ok)
        {
            return checkout;
        }

        await services.GetRequiredService<BusRepository>().PullAsync(ct);
        IReadOnlyList<MandateLedger> ledgers;
        try
        {
            ledgers = await services.GetRequiredService<BusInventory>().MandatesAsync(ct);
        }
        catch (BusBusyException ex)
        {
            return BusHost.ConfigurationError(io, ex.Message);
        }

        int exit = ExitCodes.Ok;
        MandateGranter granter = services.GetRequiredService<MandateGranter>();
        foreach (MandateLedger ledger in ledgers.Where(l => l is { FilePresent: true, Revoked: false, Mandate: not null }))
        {
            switch (await granter.RevokeAsync(ledger.Id, ct))
            {
                case AskDecidePublished { Pushed: true }:
                    await io.Out.WriteLineAsync("revoked " + ledger.Id.Value);
                    break;
                case AskDecidePublished:
                    await io.Out.WriteLineAsync("revoked " + ledger.Id.Value + "  (not pushed yet)");
                    exit = exit == ExitCodes.Ok ? ExitCodes.PushDeferred : exit;
                    break;
                case var other:
                    await io.Error.WriteLineAsync("zyggy: mandate " + ledger.Id.Value + " not revoked: " + Describe(other));
                    exit = ExitCodes.Failed;
                    break;
            }
        }

        return exit;
    }

    private static async Task<int> StartAsync(CliEnvironment environment, VerbIo io, ParseResult parseResult)
    {
        if (environment.HooksOff)
        {
            await io.Error.WriteLineAsync("zyggy: refused: supervision is started again only in an attended session");
            return ExitCodes.Configuration;
        }

        (NodeConfiguration? configuration, int exitCode) = BusHost.LoadConfiguration(parseResult, environment, io);
        if (configuration is null)
        {
            return exitCode;
        }

        try
        {
            bool removed = SupervisionSwitch.Start(configuration.EffectiveNodeDir);
            await io.Out.WriteLineAsync(removed ? "started: supervised work may run on " + configuration.Machine.Value + " again" : "not stopped: nothing to start");
            return ExitCodes.Ok;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return BusHost.ConfigurationError(io, "supervise: cannot remove the stop marker in " + configuration.EffectiveNodeDir + ": " + ex.Message);
        }
    }

    private static string Describe(AskDecideResult result) => result switch
    {
        AskDecideNotFound notFound => notFound.Detail,
        AskDecideRefused refused => refused.Field + ": " + refused.Detail,
        AskDecideFailed failed => failed.Stage + ": " + failed.Detail,
        _ => result.GetType().Name,
    };
}
