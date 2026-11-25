using System.CommandLine;

using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Envelope;
using Zyggy.Core.Verbs;

namespace Zyggy.Cli.Commands;

/// <summary>
/// <c>zyggy withdraw &lt;id&gt; [--why &lt;line&gt;]</c> (spec 61 §1): on the job's sender, or on Central for any job of the tenant — a queued job is
/// removed with this machine's <c>failed</c> / <c>withdrawn</c> report, a claimed one gets a signed withdrawal its machine acts on at its next
/// pull. Exit codes: 0 withdrawn or requested, 1 not found or already reported, 2 usage, 3 configuration, 4 checkout lock busy, 5 refused,
/// 6 a git step failed, 7 committed but the push is deferred. Option reading and rendering only; the rules are Core's (<see cref="JobWithdrawer"/>).
/// </summary>
internal static class WithdrawCommand
{
    private const int MaxWhy = 200;

    public static Command Create(CliEnvironment environment, VerbIo io, Action<IServiceCollection>? configure)
    {
        var id = new Argument<string>("id") { Description = "The job id (ULID)." };
        var why = new Option<string?>("--why") { Description = "One line, at most 200 characters, why the job is withdrawn; it travels with the withdrawal and the report as data." };
        var command = new Command("withdraw", "Withdraw a job you sent (on Central: any job of the tenant): a queued job is removed with a withdrawn report; a running one is stopped by its machine, its work kept on zyggy/<id>.")
        {
            id, why, BusHost.ConfigOption, BusHost.VerboseOption,
        };
        command.SetAction((parseResult, ct) => RunAsync(environment, io, configure, parseResult, parseResult.GetValue(id)!, parseResult.GetValue(why), ct));
        return command;
    }

    private static async Task<int> RunAsync(CliEnvironment environment, VerbIo io, Action<IServiceCollection>? configure, ParseResult parseResult, string idText, string? why, CancellationToken ct)
    {
        if (!EnvelopeId.TryParse(idText, out EnvelopeId? id))
        {
            await io.Error.WriteLineAsync("zyggy: the id must be a ULID");
            return ExitCodes.Usage;
        }

        if (why is not null && (why.Length is 0 or > MaxWhy || why.Contains('\n', StringComparison.Ordinal) || why.Contains('\r', StringComparison.Ordinal)))
        {
            await io.Error.WriteLineAsync("zyggy: --why must be one line of 1 to 200 characters");
            return ExitCodes.Usage;
        }

        (NodeConfiguration? configuration, int exitCode) = BusHost.LoadConfiguration(parseResult, environment, io);
        if (configuration is null)
        {
            return exitCode;
        }

        if (configuration.SignWith is null)
        {
            return BusHost.ConfigurationError(io, "withdraw: signWith missing in node.json");
        }

        await using ServiceProvider services = BusHost.BuildServices(configuration, parseResult.GetValue(BusHost.VerboseOption), environment, configure);
        int checkout = await BusHost.CheckCheckoutAsync(services, configuration, io, ct);
        if (checkout != ExitCodes.Ok)
        {
            return checkout;
        }

        WithdrawOutcome outcome = await services.GetRequiredService<JobWithdrawer>().WithdrawAsync(id, why, ct);
        string target = outcome.Target?.Value ?? "unknown";
        string deferred = outcome.PushDeferred ? "  (not pushed yet)" : string.Empty;
        switch (outcome.Result)
        {
            case WithdrawResult.Withdrawn:
                await io.Out.WriteLineAsync("withdrawn " + id.Value + " (was queued on " + target + ")" + deferred);
                return outcome.PushDeferred ? ExitCodes.PushDeferred : ExitCodes.Ok;
            case WithdrawResult.Requested:
                await io.Out.WriteLineAsync("withdrawal requested " + id.Value + " (claimed by " + target + ")" + deferred);
                if (outcome.TargetVersion is { } version)
                {
                    await io.Out.WriteLineAsync(target + " runs " + version + ": the run is not stopped before 0.30.1");
                }

                return outcome.PushDeferred ? ExitCodes.PushDeferred : ExitCodes.Ok;
            case WithdrawResult.NotFound:
                await io.Out.WriteLineAsync(id.Value + ": not found");
                return ExitCodes.NotFound;
            case WithdrawResult.AlreadyReported:
                await io.Out.WriteLineAsync(id.Value + ": already reported (" + outcome.Detail + ")");
                return ExitCodes.NotFound;
            case WithdrawResult.SessionTask:
                await io.Error.WriteLineAsync("zyggy: " + id.Value + ": a session task; finish it with zyggy work --failed");
                return ExitCodes.Refused;
            case WithdrawResult.NotSender:
                await io.Error.WriteLineAsync("zyggy: " + id.Value + ": submitted by " + outcome.Detail + "; withdraw it there or on Central");
                return ExitCodes.Refused;
            case WithdrawResult.DoesNotVerify:
                await io.Error.WriteLineAsync("zyggy: " + id.Value + ": the job's file on " + target + " does not verify (" + outcome.Detail + "); zyggy verify names it");
                return ExitCodes.Refused;
            case WithdrawResult.Busy:
                await io.Error.WriteLineAsync("zyggy: withdraw: " + outcome.Detail);
                return ExitCodes.Locked;
            default:
                await io.Error.WriteLineAsync("zyggy: withdraw: " + outcome.Detail);
                return ExitCodes.Failed;
        }
    }
}
