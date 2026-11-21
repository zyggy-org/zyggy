using System.CommandLine;
using System.Globalization;

using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Verbs;

namespace Zyggy.Cli.Commands;

/// <summary>
/// <c>zyggy sweep [--dry-run]</c> (spec 17 CLI): on Central, pulls the bus and runs one sweep pass — stale claims re-queued, exhausted
/// or rejected, old reports and context archived — printing one line per action and a summary. <c>--dry-run</c> does not pull and
/// writes nothing: it prints the checkout's commit and what a pass would do. Ordering, exit codes and rendering only; the pass is
/// Core's (<see cref="BusSweep"/>).
/// </summary>
internal static class SweepCommand
{
    private static readonly Option<bool> DryRunOption = new("--dry-run") { Description = "Print what a pass would do against the checkout as it is; pull nothing, write nothing." };

    public static Command Create(CliEnvironment environment, VerbIo io, Action<IServiceCollection>? configure)
    {
        var command = new Command("sweep", "On Central: put stale claims back in the queue, end a job after three attempts, reject claims that do not verify, archive reports and context older than 14 days.")
        {
            BusHost.ConfigOption, BusHost.VerboseOption, DryRunOption,
        };
        command.SetAction((parseResult, ct) => RunAsync(environment, io, configure, parseResult, ct));
        return command;
    }

    /// <summary>One stdout line per settled claim (spec 17 CLI).</summary>
    internal static string ActionLine(SweepAction action) => action.Kind switch
    {
        SweepActionKind.Requeued => string.Create(CultureInfo.InvariantCulture, $"requeued {action.Id.Value} {action.Machine.Value} attempt {action.Attempt}"),
        SweepActionKind.Exhausted => string.Create(CultureInfo.InvariantCulture, $"exhausted {action.Id.Value} {action.Machine.Value} after {action.Attempt} attempts"),
        SweepActionKind.SessionExpired => $"no_session {action.Id.Value} {action.Machine.Value}",
        _ => $"rejected {action.Id.Value} {action.Machine.Value} {action.Reason}",
    };

    private static async Task<int> RunAsync(CliEnvironment environment, VerbIo io, Action<IServiceCollection>? configure, ParseResult parseResult, CancellationToken ct)
    {
        (NodeConfiguration? configuration, int exitCode) = BusHost.LoadConfiguration(parseResult, environment, io);
        if (configuration is null)
        {
            return exitCode;
        }

        // There is no role key: Central is the machine named central, the same rule the node's pickup uses.
        if (configuration.Machine != MachineName.Central)
        {
            await io.Error.WriteLineAsync("sweep: only Central sweeps (role is node)");
            return ExitCodes.Configuration;
        }

        bool dryRun = parseResult.GetValue(DryRunOption);
        await using ServiceProvider services = BusHost.BuildServices(configuration, parseResult.GetValue(BusHost.VerboseOption), environment, s =>
        {
            s.AddBusSweep();
            configure?.Invoke(s);
        });

        int checkout = await BusHost.CheckCheckoutAsync(services, configuration, io, ct);
        if (checkout != ExitCodes.Ok)
        {
            return checkout;
        }

        BusRepository bus = services.GetRequiredService<BusRepository>();
        if (dryRun)
        {
            string? head = await bus.HeadShaAsync(ct);
            await io.Out.WriteLineAsync("checkout at " + (head is null ? "unknown" : head[..Math.Min(7, head.Length)]));
        }
        else
        {
            BusSyncResult pull = await bus.PullAsync(ct);
            if (!pull.Succeeded)
            {
                bool busy = pull.Detail.StartsWith(BusRepository.BusyDetail, StringComparison.Ordinal);
                await io.Error.WriteLineAsync("zyggy: sweep: " + (busy ? pull.Detail : "pull failed: " + pull.Detail));
                return busy ? ExitCodes.Locked : ExitCodes.Failed;
            }
        }

        SweepResult result = await services.GetRequiredService<BusSweep>().RunAsync(dryRun, ct);
        foreach (SweepAction action in result.Actions)
        {
            await io.Out.WriteLineAsync(ActionLine(action));
        }

        if (result.OldestArchived is { } oldest)
        {
            await io.Out.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture, $"archived {result.Archived} file(s), oldest created {oldest.UtcDateTime:yyyy-MM-dd'T'HH:mm:ss'Z'}"));
        }

        await io.Out.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"{(dryRun ? "dry run: " : string.Empty)}sweep: {result.Requeued} re-queued, {result.Exhausted} exhausted, {result.Rejected} rejected, {result.Archived} archived"));
        if (result.Failure is { } failure)
        {
            await io.Error.WriteLineAsync($"zyggy: sweep: failed at {failure.Stage}: {failure.Detail}");
            return failure.Stage == "lock" ? ExitCodes.Locked : ExitCodes.Failed;
        }

        return result.PushDeferred ? ExitCodes.PushDeferred : ExitCodes.Ok;
    }
}
