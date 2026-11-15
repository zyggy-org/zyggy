using System.CommandLine;
using System.Globalization;
using System.Text;

using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Envelope;
using Zyggy.Core.Memory;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Verbs;

namespace Zyggy.Cli.Commands;

/// <summary>
/// <c>zyggy decide</c> and <c>zyggy decisions</c> (deliverable 18, "Deciding" and "Record"): Central answers a question, report or job
/// with one signed decision — the answer and the record at once — and any machine lists the decisions, the archived ones included.
/// Ordering, option reading and rendering only — every decision is in Core (<see cref="DecisionWriter"/>, <see cref="BusInventory"/>).
/// </summary>
internal static class DecideCommand
{
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    public static Command Create(CliEnvironment environment, VerbIo io, Action<IServiceCollection>? configure)
    {
        var replyTo = new Option<string?>("--reply-to") { Description = "The id of the question, report or job this decision settles." };
        var kind = new Option<string?>("--kind") { Description = "answer, accept, correct, next, stop, escalate, refuse or resume." };
        var basis = new Option<string?>("--basis") { Description = "verified (you read the result of a check that ran) or reported (you only read an agent's account)." };
        var mandate = new Option<string?>("--mandate") { Description = "The mandate the decision acts under." };
        var step = new Option<string?>("--step") { Description = "The step concerned (1-60 lowercase letters, digits, spaces or hyphens)." };
        var evidence = new Option<string[]>("--evidence") { Description = "A report id or a commit sha the decision rests on; may be repeated up to 10 times.", AllowMultipleArgumentsPerToken = false, Arity = ArgumentArity.ZeroOrMore };
        var undo = new Option<string?>("--undo") { Description = "One line: how to reverse the decision, or 'nothing to undo'." };
        var text = new Argument<string?>("text") { Description = "What was decided and why, at most 1200 characters.", Arity = ArgumentArity.ZeroOrOne };
        var command = new Command("decide", "Central only: answer a question, report or job with one signed decision (the answer and the record at once).")
        {
            replyTo, kind, basis, mandate, step, evidence, undo, text, BusHost.ConfigOption, BusHost.VerboseOption,
        };
        command.SetAction((parseResult, ct) => DecideAsync(
            environment,
            io,
            configure,
            parseResult,
            new Given(
                parseResult.GetValue(replyTo),
                parseResult.GetValue(kind),
                parseResult.GetValue(basis),
                parseResult.GetValue(mandate),
                parseResult.GetValue(step),
                parseResult.GetValue(evidence) ?? [],
                parseResult.GetValue(undo),
                parseResult.GetValue(text)),
            ct));
        return command;
    }

    public static Command CreateList(CliEnvironment environment, VerbIo io, Action<IServiceCollection>? configure)
    {
        var since = new Option<string?>("--since") { Description = "Only decisions from this day on (YYYY-MM-DD)." };
        var mandate = new Option<string?>("--mandate") { Description = "Only decisions under this mandate." };
        var command = new Command("decisions", "List Central's decisions, newest first, with kind, basis, evidence and undo; the archived ones included.")
        {
            since, mandate, BusHost.ConfigOption, BusHost.VerboseOption,
        };
        command.SetAction((parseResult, ct) => ListAsync(environment, io, configure, parseResult, parseResult.GetValue(since), parseResult.GetValue(mandate), ct));
        return command;
    }

    /// <summary>The record as <c>zyggy decisions</c> prints it: one block per decision, newest first.</summary>
    internal static string Render(IReadOnlyList<DecisionRecord> decisions)
    {
        if (decisions.Count == 0)
        {
            return "no decisions\n";
        }

        var sb = new StringBuilder();
        foreach (DecisionRecord d in decisions)
        {
            sb.Append(d.Id.Value).Append("  ").Append(d.Created.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture))
                .Append("  ").Append(EnvelopeWire.ToWire(d.Fields.Kind)).Append("  ").Append(EnvelopeWire.ToWire(d.Fields.Basis))
                .Append("  central→").Append(d.To.Value).Append("  in reply to ").Append(d.InReplyTo.Value);
            if (d.Fields.Project is not null)
            {
                sb.Append("  project ").Append(d.Fields.Project);
            }

            if (d.Fields.Step is not null)
            {
                sb.Append("  step ").Append(d.Fields.Step);
            }

            if (d.Fields.Mandate is { } mandate)
            {
                sb.Append("  mandate ").Append(mandate.Value);
            }

            if (d.Archived)
            {
                sb.Append("  (archived)");
            }

            sb.Append('\n');
            if (d.Fields.Evidence is { Count: > 0 } evidence)
            {
                sb.Append("  evidence: ").AppendJoin(", ", evidence).Append('\n');
            }

            sb.Append("  undo: ").Append(d.Fields.Undo).Append('\n');
            foreach (string line in d.Text.TrimEnd('\n').Split('\n'))
            {
                sb.Append("  ").Append(line).Append('\n');
            }
        }

        return sb.ToString();
    }

    private static async Task<int> DecideAsync(CliEnvironment environment, VerbIo io, Action<IServiceCollection>? configure, ParseResult parseResult, Given given, CancellationToken ct)
    {
        EnvelopeId? mandate = null;
        if (!EnvelopeId.TryParse(given.ReplyTo, out EnvelopeId? replyTo)
            || !EnvelopeWire.TryFromWire(given.Kind, out DecisionKind kind)
            || !EnvelopeWire.TryFromWire(given.Basis, out DecisionBasis basis)
            || (given.Mandate is not null && !EnvelopeId.TryParse(given.Mandate, out mandate))
            || string.IsNullOrWhiteSpace(given.Undo)
            || string.IsNullOrWhiteSpace(given.Text))
        {
            await io.Error.WriteLineAsync("zyggy: decide: give --reply-to <id>, --kind, --basis, --undo <line> and the text; --kind answer|accept|correct|next|stop|escalate|refuse|resume, --basis verified|reported");
            return ExitCodes.Usage;
        }

        (NodeConfiguration? configuration, int exitCode) = BusHost.LoadConfiguration(parseResult, environment, io);
        if (configuration is null)
        {
            return exitCode;
        }

        if (configuration.Machine != MachineName.Central)
        {
            await io.Error.WriteLineAsync("zyggy: refused: only central decides");
            return ExitCodes.Configuration;
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

        try
        {
            // The question may have reached the remote a moment ago; a failed pull leaves the checkout as it was and the lookup says so.
            await services.GetRequiredService<BusRepository>().PullAsync(ct);
            DecisionTarget? target = await services.GetRequiredService<BusInventory>().FindDecidableAsync(replyTo, ct);
            if (target is null)
            {
                await io.Error.WriteLineAsync("zyggy: decide: nothing on the bus has that id");
                return ExitCodes.NotFound;
            }

            var request = new DecisionRequest(replyTo, target.Machine, kind, basis, given.Text, given.Undo, mandate, target.Project, given.Step, given.Evidence.Length > 0 ? given.Evidence : null);
            AskDecideResult result = await services.GetRequiredService<DecisionWriter>().DecideAsync(request, target.Question, ct);
            switch (result)
            {
                case AskDecidePublished { Pushed: true } published:
                    await io.Out.WriteLineAsync("decided " + published.Id.Value + "  central→" + target.Machine.Value);
                    return ExitCodes.Ok;
                case AskDecidePublished deferred:
                    await io.Out.WriteLineAsync("decided " + deferred.Id.Value + "  central→" + target.Machine.Value + "  (not pushed yet)");
                    return ExitCodes.PushDeferred;
                case AskDecideRefused { Field: "machine" } machine:
                    await io.Error.WriteLineAsync("zyggy: refused: " + machine.Detail);
                    return ExitCodes.Configuration;
                case AskDecideRefused refused:
                    await io.Error.WriteLineAsync("zyggy: refused: " + refused.Field + ": " + refused.Detail);
                    return ExitCodes.Refused;
                default:
                    await io.Error.WriteLineAsync("zyggy: decision not written: " + (result is AskDecideFailed failed ? failed.Stage + ": " + failed.Detail : result.GetType().Name));
                    return ExitCodes.Failed;
            }
        }
        catch (BusBusyException ex)
        {
            return BusHost.ConfigurationError(io, ex.Message);
        }
    }

    private static async Task<int> ListAsync(CliEnvironment environment, VerbIo io, Action<IServiceCollection>? configure, ParseResult parseResult, string? sinceText, string? mandateText, CancellationToken ct)
    {
        DateTimeOffset? since = null;
        if (sinceText is not null)
        {
            if (!DateOnly.TryParseExact(sinceText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly day))
            {
                await io.Error.WriteLineAsync("zyggy: decisions: --since must be a day, YYYY-MM-DD");
                return ExitCodes.Usage;
            }

            since = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        }

        EnvelopeId? mandate = null;
        if (mandateText is not null && !EnvelopeId.TryParse(mandateText, out mandate))
        {
            await io.Error.WriteLineAsync("zyggy: decisions: --mandate must be a ULID");
            return ExitCodes.Usage;
        }

        (NodeConfiguration? configuration, int exitCode) = BusHost.LoadConfiguration(parseResult, environment, io);
        if (configuration is null)
        {
            return exitCode;
        }

        await using ServiceProvider services = BusHost.BuildServices(configuration, parseResult.GetValue(BusHost.VerboseOption), environment, configure);
        int checkout = await BusHost.CheckCheckoutAsync(services, configuration, io, ct);
        if (checkout != ExitCodes.Ok)
        {
            return checkout;
        }

        try
        {
            // A laptop without the node service has no other pull; when the bus cannot be reached the record is shown as the checkout has it.
            BusSyncResult pulled = await services.GetRequiredService<BusRepository>().PullAsync(ct);
            if (!pulled.Succeeded)
            {
                await io.Error.WriteLineAsync("zyggy: decisions: bus not pulled, showing the checkout as it is");
            }

            await io.Out.WriteAsync(Render(await services.GetRequiredService<BusInventory>().DecisionsAsync(since, mandate, ct)));
            return ExitCodes.Ok;
        }
        catch (BusBusyException ex)
        {
            return BusHost.ConfigurationError(io, ex.Message);
        }
    }

    private sealed record Given(string? ReplyTo, string? Kind, string? Basis, string? Mandate, string? Step, string[] Evidence, string? Undo, string? Text);
}
