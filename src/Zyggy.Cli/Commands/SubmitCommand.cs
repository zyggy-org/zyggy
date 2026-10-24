using System.CommandLine;

using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
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
        var project = new Option<string>("--project") { Description = "The project: a registry name or an absolute path on the target.", Required = true };
        var agent = new Option<string?>("--agent") { Description = "The subagent to run (optional)." };
        var timeout = new Option<int?>("--timeout") { Description = "The time limit in minutes (1–1440; the node's default when absent)." };
        var allowedTools = new Option<string[]>("--allowed-tools") { Description = "Tool approval rules, comma-separated; may be repeated.", AllowMultipleArgumentsPerToken = false, Arity = ArgumentArity.ZeroOrMore };
        var priority = new Option<string?>("--priority") { Description = "low, normal or high (normal when absent)." };
        var body = new Argument<string?>("body") { Description = "The job's instructions; read from standard input when absent.", Arity = ArgumentArity.ZeroOrOne };
        var command = new Command("submit", "Build, sign and publish a job envelope to a machine's inbox on the bus.")
        {
            to, project, agent, timeout, allowedTools, priority, body, BusHost.ConfigOption, BusHost.VerboseOption,
        };
        command.SetAction((parseResult, ct) => RunAsync(
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
            parseResult.GetValue(body),
            ct));
        return command;
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
        string? positionalBody,
        CancellationToken ct)
    {
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

        string? bodyText = positionalBody;
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

        SubmitResult result = await services.GetRequiredService<JobSubmitter>().SubmitAsync(request!, ct);
        switch (result)
        {
            case Submitted submitted:
                await io.Out.WriteLineAsync($"submitted {submitted.Id.Value} to {request!.To.Value} ({submitted.RelativePath})");
                return ExitCodes.Ok;
            case SubmitDeferred deferred:
                await io.Out.WriteLineAsync($"submitted {deferred.Id.Value} to {request!.To.Value} ({deferred.RelativePath}) (committed locally, not yet pushed: the next bus write on this machine pushes it)");
                return ExitCodes.PushDeferred;
            case SubmitFailed { Stage: "sign" }:
                await io.Error.WriteLineAsync($"zyggy: signing key {configuration.SignWith} is not in the secret store; run: zyggy secret set {configuration.SignWith.SecretName} --hex");
                return ExitCodes.Failed;
            case SubmitFailed { Stage: "lock" }:
                await io.Error.WriteLineAsync("zyggy: the bus checkout is busy: another zyggy process held its lock");
                return ExitCodes.Failed;
            case SubmitFailed failed:
                await io.Error.WriteLineAsync($"zyggy: submit failed at {failed.Stage}: {failed.Detail}");
                return ExitCodes.Failed;
            case SubmitRefused refused:
                await io.Error.WriteLineAsync($"zyggy: refusing to submit: the envelope does not round-trip ({refused.Field})");
                return ExitCodes.Usage;
            default:
                await io.Error.WriteLineAsync("zyggy: submit failed: " + result.GetType().Name);
                return ExitCodes.Failed;
        }
    }
}
