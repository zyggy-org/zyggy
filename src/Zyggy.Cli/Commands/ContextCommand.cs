using System.CommandLine;

using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Memory;
using Zyggy.Core.Verbs;

namespace Zyggy.Cli.Commands;

/// <summary>
/// <c>zyggy context</c> (founding spec §3 and §4 as amended by deliverable 12): a laptop's path into Central's memory. The explicit
/// form sends one stated fact as a signed context envelope; <c>--stop-hook</c> spools a turn's note and <c>--flush</c> ships the
/// session's spool (the laptop hooks). Ordering, option reading and rendering only — every decision is in Core
/// (<see cref="ContextRequest"/>, <see cref="ContextSubmitter"/>). The fact is echoed once as the stored line, nowhere else.
/// </summary>
internal static class ContextCommand
{
    public static Command Create(CliEnvironment environment, VerbIo io, Action<IServiceCollection>? configure)
    {
        var scope = new Option<string?>("--scope") { Description = "general (default), project:<name> or machine." };
        var stopHook = new Option<bool>("--stop-hook") { Description = "Spool the turn's note from the Stop hook JSON on stdin (the laptop hook; never by hand)." };
        var flush = new Option<bool>("--flush") { Description = "Ship the session's spool from the SessionEnd JSON on stdin as one signed context envelope (the laptop hook)." };
        var fact = new Argument<string?>("fact") { Description = "The fact, one sentence; read from standard input when absent.", Arity = ArgumentArity.ZeroOrOne };
        var command = new Command("context", "Send one stated fact to Central's memory as a signed context envelope (remember skill on laptops); --stop-hook spools a turn's note, --flush ships the session's spool (the laptop hooks).")
        {
            scope, stopHook, flush, fact, BusHost.ConfigOption, BusHost.VerboseOption,
        };
        command.SetAction((parseResult, ct) => RunAsync(
            environment,
            io,
            configure,
            parseResult,
            parseResult.GetValue(scope),
            parseResult.GetValue(stopHook),
            parseResult.GetValue(flush),
            parseResult.GetValue(fact),
            ct));
        return command;
    }

    private static async Task<int> RunAsync(
        CliEnvironment environment,
        VerbIo io,
        Action<IServiceCollection>? configure,
        ParseResult parseResult,
        string? scope,
        bool stopHook,
        bool flush,
        string? positionalFact,
        CancellationToken ct)
    {
        if (environment.Variables.TryGetValue("ZYGGY_HOOKS", out string? hooks) && hooks == "off")
        {
            return ExitCodes.Ok;
        }

        bool explicitFact = positionalFact is not null || (!stopHook && !flush && io.InputRedirected);
        if ((stopHook ? 1 : 0) + (flush ? 1 : 0) + (explicitFact ? 1 : 0) > 1)
        {
            await io.Error.WriteLineAsync("zyggy: mode: --stop-hook, --flush and a fact are mutually exclusive");
            return ExitCodes.Usage;
        }

        (NodeConfiguration? configuration, int exitCode) = BusHost.LoadConfiguration(parseResult, environment, io);
        if (configuration is null)
        {
            return exitCode;
        }

        if (ContextRequest.CheckSender(configuration.Machine) is { } sender)
        {
            await io.Error.WriteLineAsync($"zyggy: {sender.Field}: {sender.Detail}");
            return ExitCodes.Usage;
        }

        if (!stopHook && configuration.SignWith is null)
        {
            return BusHost.ConfigurationError(io, "signWith missing");
        }

        string? patternsError = null;
        await using ServiceProvider services = BusHost.BuildServices(configuration, parseResult.GetValue(BusHost.VerboseOption), environment, s =>
        {
            patternsError = s.AddSecretPatterns(environment.Variables);
            configure?.Invoke(s);
        });
        if (patternsError is not null)
        {
            return BusHost.ConfigurationError(io, patternsError);
        }

        if (stopHook)
        {
            // D2: the hook touches no checkout, no key and no bus — only the spool under nodeDir.
            return await RenderStopHookAsync(io, services.GetRequiredService<ContextStopHook>().Run(io.In, io.InputRedirected));
        }

        int checkout = await BusHost.CheckCheckoutAsync(services, configuration, io, ct);
        if (checkout != ExitCodes.Ok)
        {
            return checkout;
        }

        if (flush)
        {
            // A15: without a redirected stdin there is no ending session — the stale spools only.
            string? sessionId = io.InputRedirected ? SessionEndInput.SessionId(io.In) : null;
            IReadOnlyList<FlushOutcome> outcomes = await services.GetRequiredService<ContextFlusher>().FlushAsync(sessionId, ct);
            return await RenderFlushAsync(io, configuration, outcomes);
        }

        string? factText = positionalFact;
        if (factText is null)
        {
            if (!io.InputRedirected)
            {
                await io.Error.WriteLineAsync("zyggy: fact: give the fact as the last argument or on stdin");
                return ExitCodes.Usage;
            }

            factText = await io.In.ReadToEndAsync(ct);
        }

        // An argument fact never reads stdin: Claude Code's shell always redirects it, and a pipe left open would hang the verb.

        TimeProvider clock = services.GetRequiredService<TimeProvider>();
        ContextFactError? error = ContextRequest.TryCreateStated(scope, factText, DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime), out ContextRequest? request);
        if (error is not null)
        {
            await io.Error.WriteLineAsync($"zyggy: {error.Field}: {error.Detail}");
            return ExitCodes.Usage;
        }

        SubmitResult result = await services.GetRequiredService<ContextSubmitter>().SubmitAsync(request!, ct);
        return await RenderAsync(io, configuration, result, request!.Lines[0]);
    }

    /// <summary>Renders the hook outcome: never stdout, never a non-zero exit (A9); one stderr line when something was refused, unreadable or capped.</summary>
    private static async Task<int> RenderStopHookAsync(VerbIo io, StopHookOutcome outcome)
    {
        switch (outcome.Result)
        {
            case StopHookResult.Unparseable:
                await io.Error.WriteLineAsync("context: unparseable hook input");
                break;
            case StopHookResult.Refused:
                await io.Error.WriteLineAsync($"context: note refused (pattern {outcome.PatternName})");
                break;
            case StopHookResult.CapReached:
                await io.Error.WriteLineAsync($"context: cap of 150 hook lines reached for session {outcome.SessionId8}");
                break;
            default:
                break;
        }

        return ExitCodes.Ok;
    }

    /// <summary>Renders the flush outcomes in order (A8): the dropped patterns, then the ship line; exit 6 over 2 over 7 over 0.</summary>
    private static async Task<int> RenderFlushAsync(VerbIo io, NodeConfiguration configuration, IReadOnlyList<FlushOutcome> outcomes)
    {
        int exit = ExitCodes.Ok;
        foreach (FlushOutcome outcome in outcomes)
        {
            foreach (string pattern in outcome.DroppedPatterns)
            {
                await io.Error.WriteLineAsync($"context: line dropped (pattern {pattern})");
            }

            if (outcome.Result is null)
            {
                continue;
            }

            int code = await RenderAsync(io, configuration, outcome.Result, storedLine: null);
            exit = Worst(exit, code);
        }

        return exit;
    }

    /// <summary>The flush's exit precedence: a failure (6) over a refusal (2) over a deferral (7) over success (0).</summary>
    private static int Worst(int current, int next)
    {
        static int Rank(int code) => code switch
        {
            ExitCodes.Failed => 3,
            ExitCodes.Usage => 2,
            ExitCodes.PushDeferred => 1,
            _ => 0,
        };

        return Rank(next) > Rank(current) ? next : current;
    }

    /// <summary>Renders one submit outcome: the stored line follows the result line on success and deferral when given; every failure is one stderr line.</summary>
    internal static async Task<int> RenderAsync(VerbIo io, NodeConfiguration configuration, SubmitResult result, string? storedLine)
    {
        switch (result)
        {
            case Submitted submitted:
                await io.Out.WriteLineAsync($"context {submitted.Id.Value} to central ({submitted.RelativePath})");
                await WriteStoredLineAsync(io, storedLine);
                return ExitCodes.Ok;
            case SubmitDeferred deferred:
                await io.Out.WriteLineAsync($"context {deferred.Id.Value} to central ({deferred.RelativePath}) (committed locally, not yet pushed: the next bus write on this machine pushes it)");
                await WriteStoredLineAsync(io, storedLine);
                return ExitCodes.PushDeferred;
            case ContextRefused refused:
                await io.Error.WriteLineAsync($"zyggy: refused: matches secret pattern {refused.PatternName}");
                return ExitCodes.Refused;
            case SubmitFailed { Stage: "sign" }:
                await io.Error.WriteLineAsync($"zyggy: signing key {configuration.SignWith} is not in the secret store; run: zyggy secret set {configuration.SignWith!.SecretName} --hex");
                return ExitCodes.Failed;
            case SubmitFailed { Stage: "secret-store" } unavailable:
                await io.Error.WriteLineAsync($"zyggy: secret store unavailable: {unavailable.Detail}");
                return ExitCodes.Failed;
            case SubmitFailed { Stage: "lock" }:
                await io.Error.WriteLineAsync("zyggy: the bus checkout is busy: another zyggy process held its lock");
                return ExitCodes.Failed;
            case SubmitFailed failed:
                await io.Error.WriteLineAsync($"zyggy: context failed at {failed.Stage}: {failed.Detail}");
                return ExitCodes.Failed;
            case SubmitRefused selfCheck:
                await io.Error.WriteLineAsync($"zyggy: refusing context: the envelope does not round-trip ({selfCheck.Field})");
                return ExitCodes.Usage;
            default:
                await io.Error.WriteLineAsync("zyggy: context failed: " + result.GetType().Name);
                return ExitCodes.Failed;
        }
    }

    private static async Task WriteStoredLineAsync(VerbIo io, string? storedLine)
    {
        if (storedLine is not null)
        {
            await io.Out.WriteLineAsync(storedLine);
        }
    }
}
