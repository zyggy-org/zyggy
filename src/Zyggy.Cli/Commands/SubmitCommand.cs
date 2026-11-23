using System.CommandLine;

using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Envelope;
using Zyggy.Core.Registry;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Verbs;

namespace Zyggy.Cli.Commands;

/// <summary>
/// <c>zyggy submit</c> (founding spec §3, §4 row 1; spec 08 AC-7/AC-8): the human entry point to the signing path. Ordering, option
/// reading and rendering only — every decision is in Core (<see cref="JobRequest.TryCreate"/>, <see cref="JobSubmitter"/>). The body is
/// never echoed; nothing but the result line reaches standard output.
/// </summary>
internal static class SubmitCommand
{
    public static Command Create(CliEnvironment environment, VerbIo io, Action<IServiceCollection>? configure)
    {
        var to = new Option<string>("--to") { Description = "The target machine (a lowercase label).", Required = true };
        var project = new Option<string?>("--project") { Description = "The project: a registry name or an absolute path on the target (required without --mandate)." };
        var agent = new Option<string?>("--agent") { Description = "The subagent to run (optional)." };
        var timeout = new Option<int?>("--timeout") { Description = "The time limit in minutes (1–1440; the node's default when absent)." };
        var allowedTools = new Option<string[]>("--allowed-tools") { Description = "Tool approval rules, comma-separated; may be repeated.", AllowMultipleArgumentsPerToken = false, Arity = ArgumentArity.ZeroOrMore };
        var priority = new Option<string?>("--priority") { Description = "low, normal or high (normal when absent)." };
        var worktree = new Option<bool>("--worktree") { Description = "Run in a git worktree on branch zyggy/<id> and push the result to the project's origin (the project needs a default branch)." };
        var branch = new Option<string?>("--branch") { Description = "With --worktree: start from this branch on the project's origin when it exists and push a successful run to it (never the default branch)." };
        var description = new Option<string?>("--description") { Description = "One line saying what the branch holds after the job: the node's commit subject and a report line." };
        var body = new Argument<string?>("body") { Description = "The job's instructions; read from standard input when absent.", Arity = ArgumentArity.ZeroOrOne };
        var mandate = new Option<string?>("--mandate") { Description = "Central: submit under this mandate; the body is the kind's shipped template, the project and tools are the mandate's." };
        var kind = new Option<string?>("--kind") { Description = "With --mandate: brief, build, verify, correct, merge, release or deploy." };
        var step = new Option<string?>("--step") { Description = "With --mandate: the step label (1-60 lowercase letters, digits, spaces or hyphens)." };
        var reference = new Option<string?>("--ref") { Description = "With --mandate: the id the template names (the verify report for --kind correct). With --fetch: the branch to read (the project's default branch when absent)." };
        var fetch = new Option<string?>("--fetch") { Description = "A fetch job: the node reads this repository-relative file at origin/<--ref> and attaches it to its report; no model runs." };
        var commandName = new Option<string?>("--command") { Description = "A command job: the node runs the argv this name has in its own node.json commands, in the project folder, without a shell; no model runs." };
        var baseBranch = new Option<string?>("--base") { Description = "With --mandate: the branch the job starts from and pushes back to; one of the mandate's branches." };
        var session = new Option<bool>("--session") { Description = "With --mandate: deliver the task into a live session (the mandate must allow sessions)." };
        var maxTurns = new Option<int?>("--max-turns") { Description = "A model job's own turn limit (at least 1); the node lowers it to its claude.maxTurns and says so." };
        var continueFrom = new Option<string?>("--continue") { Description = "With --worktree: start from branch zyggy/<job-id> of this earlier job on the project's origin (also under a mandate, same step)." };
        var model = new Option<string?>("--model") { Description = "The full Claude Code model id the job must run on (never an alias); the target's node must publish the capability model." };
        var command = new Command("submit", "Build, sign and publish a job envelope to a machine's inbox on the bus.")
        {
            to, project, agent, timeout, allowedTools, priority, worktree, branch, description, body, mandate, kind, step, reference, baseBranch, session, fetch, commandName, maxTurns, continueFrom, model, BusHost.ConfigOption, BusHost.VerboseOption,
        };
        command.SetAction((parseResult, ct) => parseResult.GetValue(mandate) is not null && parseResult.GetValue(model) is not null
            ? RefuseModelUnderMandateAsync(io)
            : parseResult.GetValue(mandate) is not null
            ? MandateAsync(
                environment,
                io,
                configure,
                parseResult,
                new MandateGiven(
                    parseResult.GetValue(to),
                    parseResult.GetValue(mandate),
                    parseResult.GetValue(kind),
                    parseResult.GetValue(step),
                    parseResult.GetValue(reference),
                    parseResult.GetValue(baseBranch),
                    parseResult.GetValue(session),
                    parseResult.GetValue(continueFrom),
                    parseResult.GetValue(maxTurns),
                    parseResult.GetValue(body) is not null || parseResult.GetValue(project) is not null || parseResult.GetValue(agent) is not null
                        || parseResult.GetValue(timeout) is not null || parseResult.GetValue(allowedTools) is { Length: > 0 } || parseResult.GetValue(worktree)
                        || parseResult.GetValue(priority) is not null || parseResult.GetValue(branch) is not null || parseResult.GetValue(description) is not null
                        || parseResult.GetValue(fetch) is not null || parseResult.GetValue(commandName) is not null),
                ct)
            : RunAsync(
            environment,
            io,
            configure,
            parseResult,
            parseResult.GetValue(to),
            parseResult.GetValue(project),
            parseResult.GetValue(agent),
            parseResult.GetValue(timeout),
            parseResult.GetValue(allowedTools),
            parseResult.GetValue(priority),
            parseResult.GetValue(worktree),
            parseResult.GetValue(branch),
            parseResult.GetValue(description),
            new FetchGiven(parseResult.GetValue(fetch), parseResult.GetValue(reference), parseResult.GetValue(commandName)),
            new ContinueGiven(parseResult.GetValue(continueFrom), parseResult.GetValue(maxTurns), parseResult.GetValue(model)),
            parseResult.GetValue(body),
            ct));
        return command;
    }

    // Spec 50 (Q6): under a mandate the model comes from the owner's table by kind, never from one submit.
    private static async Task<int> RefuseModelUnderMandateAsync(VerbIo io)
    {
        await io.Error.WriteLineAsync("zyggy: submit: --model is not allowed with --mandate: under a mandate the model comes from the table by kind");
        return ExitCodes.Usage;
    }

    private static async Task<int> RunAsync(
        CliEnvironment environment,
        VerbIo io,
        Action<IServiceCollection>? configure,
        ParseResult parseResult,
        string? to,
        string? project,
        string? agent,
        int? timeout,
        string[]? allowedTools,
        string? priority,
        bool worktree,
        string? branch,
        string? description,
        FetchGiven fetch,
        ContinueGiven continued,
        string? positionalBody,
        CancellationToken ct)
    {
        if (fetch.Path is null && fetch.Reference is not null)
        {
            await io.Error.WriteLineAsync("zyggy: submit: --ref needs --mandate or --fetch");
            return ExitCodes.Usage;
        }

        if (fetch.Path is not null && fetch.Command is not null)
        {
            await io.Error.WriteLineAsync("zyggy: submit: --fetch and --command exclude each other");
            return ExitCodes.Usage;
        }

        // Deliverable 18: an unattended run submits only under a mandate (spec 18, "Enforced on Central").
        if (environment.HooksOff)
        {
            await io.Error.WriteLineAsync("zyggy: refused: an unattended run submits only under a mandate (--mandate)");
            return ExitCodes.Configuration;
        }

        if (project is null)
        {
            await io.Error.WriteLineAsync("zyggy: submit: --project is required without --mandate");
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

        await using ServiceProvider services = BusHost.BuildServices(configuration, parseResult.GetValue(BusHost.VerboseOption), environment, configure);
        int checkout = await BusHost.CheckCheckoutAsync(services, configuration, io, ct);
        if (checkout != ExitCodes.Ok)
        {
            return checkout;
        }

        // A fetch or command job runs no model: its body is only the trace line, written here when none is given.
        string? bodyText = positionalBody
            ?? (fetch.Path is not null ? "fetch " + fetch.Path + "\n" : fetch.Command is not null ? "command " + fetch.Command + "\n" : null);
        if (bodyText is null)
        {
            if (!io.InputRedirected)
            {
                await io.Error.WriteLineAsync("zyggy: body: give the body as the last argument or on stdin");
                return ExitCodes.Usage;
            }

            bodyText = await io.In.ReadToEndAsync(ct);
        }

        JobRequestError? error = JobRequest.TryCreate(to, project, agent, timeout, allowedTools is { Length: > 0 } ? allowedTools : null, priority, bodyText, out JobRequest? request);
        if (error is not null)
        {
            await io.Error.WriteLineAsync($"zyggy: {error.Field}: {error.Detail}");
            return ExitCodes.Usage;
        }

        RegistryDocument? targetRegistry = branch is null ? null : await ReadRegistryAsync(configuration, request!.To, ct);
        error = request!.TryWithBranch(worktree, branch, description, targetRegistry, out JobRequest? named);
        if (error is not null)
        {
            await io.Error.WriteLineAsync($"zyggy: {error.Field}: {error.Detail}");
            return ExitCodes.Usage;
        }

        // Spec 59 slice 6: --continue and --max-turns, checked in Core before the kind (a fetch or command job refuses them there).
        error = named!.TryWithContinue(continued.Continue, continued.MaxTurns, out named);
        if (error is not null)
        {
            await io.Error.WriteLineAsync($"zyggy: {error.Field}: {error.Detail}");
            return ExitCodes.Usage;
        }

        if (fetch.Path is not null)
        {
            error = named!.TryWithFetch(fetch.Path, fetch.Reference, out named);
            if (error is not null)
            {
                await io.Error.WriteLineAsync($"zyggy: {error.Field}: {error.Detail}");
                return ExitCodes.Usage;
            }
        }

        if (fetch.Command is not null)
        {
            error = named!.TryWithCommand(fetch.Command, out named);
            if (error is not null)
            {
                await io.Error.WriteLineAsync($"zyggy: {error.Field}: {error.Detail}");
                return ExitCodes.Usage;
            }
        }

        // Spec 50: the job's model, checked after the kind (a fetch or command job runs no model); the capability is the submitter's check.
        error = named!.TryWithModel(continued.Model, out named);
        if (error is not null)
        {
            await io.Error.WriteLineAsync($"zyggy: {error.Field}: {error.Detail}");
            return ExitCodes.Usage;
        }

        SubmitResult result = await services.GetRequiredService<JobSubmitter>().SubmitAsync(named!, ct);
        int exit = await RenderAsync(io, configuration, request!.To, result);
        if (named!.Model is { } chosen && result is Submitted or SubmitDeferred)
        {
            await io.Out.WriteLineAsync($"model: {chosen} (override)");
        }

        return exit;
    }

    // Deliverable 18: Central's submit under a mandate. No body, project, tools, agent, timeout, priority or worktree may be given: the
    // template and the mandate decide them. Every limit is checked in Core; a refusal names it and writes nothing.
    private static async Task<int> MandateAsync(CliEnvironment environment, VerbIo io, Action<IServiceCollection>? configure, ParseResult parseResult, MandateGiven given, CancellationToken ct)
    {
        if (given.FreeFieldGiven)
        {
            await io.Error.WriteLineAsync("zyggy: refused: under a mandate the body, project, tools, agent, timeout, priority, worktree, branch and description come from the mandate and its template");
            return ExitCodes.Configuration;
        }

        MandateJobKind kind = default;
        EnvelopeId? reference = null;
        EnvelopeId? continueFrom = null;
        if (!MachineName.TryParse(given.To, out MachineName? to)
            || !EnvelopeId.TryParse(given.Mandate, out EnvelopeId? mandate)
            || !EnvelopeWire.TryFromWire(given.Kind, out kind)
            || string.IsNullOrEmpty(given.Step)
            || (given.Reference is not null && !EnvelopeId.TryParse(given.Reference, out reference))
            || (given.Continue is not null && !EnvelopeId.TryParse(given.Continue, out continueFrom))
            || given.MaxTurns is < 1)
        {
            await io.Error.WriteLineAsync("zyggy: submit: under a mandate give --to <machine>, --mandate <id>, --kind brief|build|verify|correct|merge|release|deploy and --step <label>; --ref and --continue take an id, --max-turns at least 1");
            return ExitCodes.Usage;
        }

        if (continueFrom is not null && given.Session)
        {
            await io.Error.WriteLineAsync("zyggy: submit: --continue needs a worktree job, not --session");
            return ExitCodes.Usage;
        }

        if (kind == MandateJobKind.Correct && reference is null)
        {
            await io.Error.WriteLineAsync("zyggy: submit: --kind correct needs --ref <the verify report's id>");
            return ExitCodes.Usage;
        }

        (NodeConfiguration? configuration, int exitCode) = BusHost.LoadConfiguration(parseResult, environment, io);
        if (configuration is null)
        {
            return exitCode;
        }

        if (configuration.Machine != MachineName.Central)
        {
            await io.Error.WriteLineAsync("zyggy: refused: only central submits under a mandate");
            return ExitCodes.Configuration;
        }

        if (configuration.SignWith is null)
        {
            return BusHost.ConfigurationError(io, "signWith missing");
        }

        await using ServiceProvider services = BusHost.BuildServices(configuration, parseResult.GetValue(BusHost.VerboseOption), environment, configure);
        int checkout = await BusHost.CheckCheckoutAsync(services, configuration, io, ct);
        if (checkout != ExitCodes.Ok)
        {
            return checkout;
        }

        MandateSubmitOutcome outcome;
        try
        {
            outcome = await services.GetRequiredService<MandateSubmission>().SubmitAsync(new MandateJobRequest(to, mandate, kind, given.Step, reference, given.Base, given.Session, continueFrom, given.MaxTurns), ct);
        }
        catch (BusBusyException ex)
        {
            return BusHost.ConfigurationError(io, ex.Message);
        }

        if (outcome.Result is null)
        {
            await io.Error.WriteLineAsync("zyggy: refused: mandate " + MandateRules.ToWire(outcome.Verdict.Limit!.Value) + ": " + outcome.Verdict.Detail);
            return ExitCodes.Configuration;
        }

        return await RenderAsync(io, configuration, to, outcome.Result);
    }

    private static async Task<int> RenderAsync(VerbIo io, NodeConfiguration configuration, MachineName to, SubmitResult result)
    {
        switch (result)
        {
            case Submitted submitted:
                await io.Out.WriteLineAsync($"submitted {submitted.Id.Value} to {to.Value} ({submitted.RelativePath})");
                return ExitCodes.Ok;
            case SubmitDeferred deferred:
                await io.Out.WriteLineAsync($"submitted {deferred.Id.Value} to {to.Value} ({deferred.RelativePath}) (committed locally, not yet pushed: the next bus write on this machine pushes it)");
                return ExitCodes.PushDeferred;
            case SubmitFailed { Stage: "sign" }:
                await io.Error.WriteLineAsync($"zyggy: signing key {configuration.SignWith} is not in the secret store; run: zyggy secret set {configuration.SignWith?.SecretName} --hex");
                return ExitCodes.Failed;
            case SubmitFailed { Stage: "secret-store" } unavailable:
                await io.Error.WriteLineAsync($"zyggy: secret store unavailable: {unavailable.Detail}");
                return ExitCodes.Failed;
            case SubmitFailed { Stage: "lock" }:
                await io.Error.WriteLineAsync("zyggy: the bus checkout is busy: another zyggy process held its lock");
                return ExitCodes.Failed;
            case SubmitFailed failed:
                await io.Error.WriteLineAsync($"zyggy: submit failed at {failed.Stage}: {failed.Detail}");
                return ExitCodes.Failed;
            case SubmitRefused { Field: "kind" } refused:
                await io.Error.WriteLineAsync("zyggy: refused: " + refused.Detail);
                return ExitCodes.Usage;
            case SubmitRefused { Field: "capability" } refused:
                // Spec 50 (AC-7): the target's node is too old to run a job with a model; exit 3, as the mandate capability refusal.
                await io.Error.WriteLineAsync("zyggy: refused: " + refused.Detail);
                return ExitCodes.Configuration;
            case SubmitRefused refused:
                await io.Error.WriteLineAsync($"zyggy: refusing to submit: the envelope does not round-trip ({refused.Field})");
                return ExitCodes.Usage;
            default:
                await io.Error.WriteLineAsync("zyggy: submit failed: " + result.GetType().Name);
                return ExitCodes.Failed;
        }
    }

    private sealed record FetchGiven(string? Path, string? Reference, string? Command);

    private sealed record ContinueGiven(string? Continue, int? MaxTurns, string? Model);

    private sealed record MandateGiven(string? To, string? Mandate, string? Kind, string? Step, string? Reference, string? Base, bool Session, string? Continue, int? MaxTurns, bool FreeFieldGiven);

    /// <summary>
    /// The target machine's registry file as it stands in this machine's bus checkout (spec 53 AC-15), or <see langword="null"/> when it
    /// is absent or unreadable: the check is then skipped and the node decides. Registry text is data; only the default branch is used.
    /// </summary>
    private static async Task<RegistryDocument?> ReadRegistryAsync(NodeConfiguration configuration, MachineName machine, CancellationToken ct)
    {
        string path = BusPaths.Resolve(configuration.Bus.LocalPath, BusPaths.Registry(configuration.Tenant, machine));
        if (!File.Exists(path))
        {
            return null;
        }

        RegistryDocument? document = RegistryDocument.Parse(await File.ReadAllBytesAsync(path, ct)).Document;
        return document is not null && document.Tenant == configuration.Tenant ? document : null;
    }
}
