using System.CommandLine;

using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Envelope;
using Zyggy.Core.Memory;
using Zyggy.Core.Verbs;

namespace Zyggy.Cli.Commands;

/// <summary>
/// <c>zyggy ask</c> (deliverable 18, "Questions and decisions"): an agent in a session or a job run asks Central one question and
/// waits for its decision. Exit 0 prints the decision; exit 8 prints <c>no answer; default: …</c> and the agent follows the default
/// it stated. Works in a job run (<c>ZYGGY_HOOKS=off</c>), which is named by <c>ZYGGY_JOB</c>. Ordering, option reading and
/// rendering only — every decision is in Core (<see cref="AskFlow"/>). The question's text is never echoed.
/// </summary>
internal static class AskCommand
{
    public static Command Create(CliEnvironment environment, VerbIo io, Action<IServiceCollection>? configure)
    {
        var wait = new Option<int?>("--wait") { Description = "Minutes to wait for Central's decision, 1-60 (default 10 in a session, 5 in a job run; never beyond the job's remaining time)." };
        var option = new Option<string[]>("--option") { Description = "One option Central may pick, one line; may be repeated up to 5 times.", AllowMultipleArgumentsPerToken = false, Arity = ArgumentArity.ZeroOrMore };
        var fallback = new Option<string?>("--default") { Description = "One line: what you do when no answer comes." };
        var question = new Argument<string?>("question") { Description = "The question, at most 600 characters.", Arity = ArgumentArity.ZeroOrOne };
        var command = new Command("ask", "Ask Central one question and wait for its decision; exit 8 with the default line when none comes in time.")
        {
            wait, option, fallback, question, BusHost.ConfigOption, BusHost.VerboseOption,
        };
        command.SetAction((parseResult, ct) => RunAsync(
            environment,
            io,
            configure,
            parseResult,
            parseResult.GetValue(wait),
            parseResult.GetValue(option) ?? [],
            parseResult.GetValue(fallback),
            parseResult.GetValue(question),
            ct));
        return command;
    }

    private static async Task<int> RunAsync(
        CliEnvironment environment,
        VerbIo io,
        Action<IServiceCollection>? configure,
        ParseResult parseResult,
        int? wait,
        string[] options,
        string? fallback,
        string? question,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(question) || string.IsNullOrWhiteSpace(fallback))
        {
            await io.Error.WriteLineAsync("zyggy: ask: give --default <line> and the question as the last argument");
            return ExitCodes.Usage;
        }

        (NodeConfiguration? configuration, int exitCode) = BusHost.LoadConfiguration(parseResult, environment, io);
        if (configuration is null)
        {
            return exitCode;
        }

        if (configuration.SignWith is null)
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

        int checkout = await BusHost.CheckCheckoutAsync(services, configuration, io, ct);
        if (checkout != ExitCodes.Ok)
        {
            return checkout;
        }

        var request = new AskRequest(
            environment.Get("CLAUDE_PROJECT_DIR") ?? Environment.CurrentDirectory,
            question,
            fallback,
            options,
            wait,
            environment.Get("ZYGGY_JOB"),
            environment.Get("ZYGGY_JOB_PROJECT"),
            environment.Get("ZYGGY_JOB_ENDS"));
        AskOutcome outcome = await services.GetRequiredService<AskFlow>().AskAsync(request, ct);
        switch (outcome)
        {
            case AskAnswered answered:
                await io.Out.WriteAsync(
                    "decision " + answered.Decision.Id.Value + "  " + EnvelopeWire.ToWire(answered.Decision.Fields.Kind) + "  " + EnvelopeWire.ToWire(answered.Decision.Fields.Basis) + "\n"
                    + answered.Decision.Text);
                return ExitCodes.Ok;
            case AskUnanswered unanswered:
                if (unanswered.Why is not null)
                {
                    await io.Error.WriteLineAsync("zyggy: " + unanswered.Why);
                }

                await io.Out.WriteAsync("no answer; default: " + unanswered.Default + "\n");
                return ExitCodes.NoAnswer;
            case AskRefused { Usage: true } usage:
                await io.Error.WriteLineAsync("zyggy: ask: " + usage.Detail);
                return ExitCodes.Usage;
            case AskRefused refused:
                await io.Error.WriteLineAsync("zyggy: refused: " + refused.Detail);
                return ExitCodes.Refused;
            default:
                await io.Error.WriteLineAsync("zyggy: question not written: " + ((outcome as AskFailed)?.Detail ?? outcome.GetType().Name));
                return ExitCodes.Failed;
        }
    }
}
