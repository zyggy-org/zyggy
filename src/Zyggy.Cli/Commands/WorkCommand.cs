using System.CommandLine;
using System.Globalization;
using System.Text;

using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Envelope;
using Zyggy.Core.Health;
using Zyggy.Core.Memory;
using Zyggy.Core.Supervision;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Verbs;

namespace Zyggy.Cli.Commands;

/// <summary>
/// <c>zyggy work</c> (deliverable 18, "Live work is visible"): without an option it prints what every machine is doing — live sessions
/// and open jobs — from the bus checkout as it is. <c>--session-start</c>, <c>--turn-end</c> and <c>--session-end</c> are the laptop
/// hooks that publish, refresh and remove a session's status; they always exit 0 and print nothing on stdout, except a decision that came after the wait of a question, once, at session start. <c>--step</c> records the
/// step a session says it is on. Ordering, option reading and rendering only — every decision is in Core
/// (<see cref="WorkStatusHook"/>, <see cref="BusInventory.WorkAsync"/>).
/// </summary>
internal static class WorkCommand
{
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    public static Command Create(CliEnvironment environment, VerbIo io, Action<IServiceCollection>? configure)
    {
        var sessionStart = new Option<bool>("--session-start") { Description = "Publish the session's status from the SessionStart hook JSON on stdin (the laptop hook; never by hand)." };
        var turnEnd = new Option<bool>("--turn-end") { Description = "Refresh the session's status from the Stop hook JSON on stdin, at most once per supervision.statusMinutes (the laptop hook)." };
        var sessionEnd = new Option<bool>("--session-end") { Description = "Remove the session's status from the SessionEnd hook JSON on stdin (the laptop hook)." };
        var step = new Option<string?>("--step") { Description = "The step this session is on (1-60 lowercase letters, digits, spaces or hyphens); travels with its next status." };
        var session = new Option<string?>("--session") { Description = "The session id --step applies to." };
        var waiting = new Option<bool>("--waiting") { Description = "Print what waits for the owner: escalated questions and reports, paused mandates; ids, kinds and names only." };
        var command = new Command("work", "Show what every machine is doing: live sessions and open jobs. --session-start, --turn-end and --session-end are the laptop hooks that publish a session's status; --step records the step a session is on.")
        {
            sessionStart, turnEnd, sessionEnd, step, session, waiting, BusHost.ConfigOption, BusHost.VerboseOption,
        };
        command.SetAction((parseResult, ct) =>
        {
            string? mode = parseResult.GetValue(sessionStart) ? "start" : parseResult.GetValue(turnEnd) ? "turn" : parseResult.GetValue(sessionEnd) ? "end" : null;
            int modes = (parseResult.GetValue(sessionStart) ? 1 : 0) + (parseResult.GetValue(turnEnd) ? 1 : 0) + (parseResult.GetValue(sessionEnd) ? 1 : 0) + (parseResult.GetValue(step) is null ? 0 : 1) + (parseResult.GetValue(waiting) ? 1 : 0);
            if (modes > 1)
            {
                io.Error.WriteLine("zyggy: mode: --session-start, --turn-end, --session-end, --step and --waiting are mutually exclusive");
                return Task.FromResult(mode is null ? ExitCodes.Usage : ExitCodes.Ok);
            }

            return mode is not null ? HookAsync(environment, io, configure, parseResult, mode, ct)
                : parseResult.GetValue(step) is { } label ? StepAsync(environment, io, configure, parseResult, parseResult.GetValue(session), label)
                : BoardAsync(environment, io, configure, parseResult, parseResult.GetValue(waiting), ct);
        });
        return command;
    }

    /// <summary>The spec's board: one block per machine, then the count of status files left out.</summary>
    internal static string Render(WorkBoard board)
    {
        var sb = new StringBuilder();
        sb.Append("work as of ").Append(board.At.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture)).Append('\n');
        if (board.Machines.Count == 0)
        {
            sb.Append("no live sessions or jobs\n");
        }

        foreach (MachineWork machine in board.Machines)
        {
            sb.Append(machine.Machine.Value).Append('\n');
            foreach (WorkSession session in machine.Sessions)
            {
                StatusFields s = session.Status;
                sb.Append("  session ").Append(s.Session).Append("  ").Append(s.Project).Append("  ")
                    .Append(session.Activity == SessionActivity.WaitingForCentral ? "waiting for Central" : session.Activity.ToString().ToLowerInvariant())
                    .Append("  last turn ").Append(HealthRules.Age(board.At, s.Activity))
                    .Append("  since ").Append(s.Since.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture));
                if (s.Branch is not null)
                {
                    sb.Append("  ").Append(s.Branch).Append(" @ ").Append(s.Head ?? "?");
                }

                if (s.Dirty is { } dirty)
                {
                    sb.Append("  ").Append(dirty.ToString(CultureInfo.InvariantCulture)).Append(" uncommitted");
                }

                if (s.Spec is not null)
                {
                    sb.Append("  ").Append(s.Spec);
                }

                if (s.Step is not null)
                {
                    sb.Append("  step ").Append(s.Step);
                }

                sb.Append('\n');
            }

            // Never the question's text: the board names what waits, `zyggy ask` and the decision carry the words.
            foreach (WorkQuestion question in machine.Questions)
            {
                sb.Append("  question ").Append(question.Id.Value).Append("  ").Append(question.Project).Append("  ")
                    .Append(question.Job is { } asker ? "job " + asker.Value : "session " + question.Session)
                    .Append("  asked ").Append(HealthRules.Age(board.At, question.Asked)).Append('\n');
            }

            foreach (WorkJob job in machine.Jobs)
            {
                sb.Append("  job ").Append(job.Id.Value).Append("  ").Append(job.Project ?? "(does not verify)").Append("  ").Append(job.Claimed ? "claimed" : "queued")
                    .Append("  submitted ").Append(HealthRules.Age(board.At, job.Submitted)).Append('\n');
            }

            if (machine.LastReport is { } report)
            {
                sb.Append("  last report ").Append(report.Id.Value).Append("  ");
                if (report.Verified && report.Status is { } status)
                {
                    sb.Append(EnvelopeWire.ToWire(status));
                    if (report.Reason is not null)
                    {
                        sb.Append(" (").Append(report.Reason).Append(')');
                    }

                    if (report.InReplyTo is { } job)
                    {
                        sb.Append("  answers ").Append(job.Value);
                    }
                }
                else
                {
                    sb.Append("(unverified: ").Append(report.Problem).Append(')');
                }

                sb.Append("  ").Append(report.Created is { } created ? HealthRules.Age(board.At, created) : "age unknown").Append('\n');
            }
        }

        if (board.StatusesNotShown > 0)
        {
            sb.Append(board.StatusesNotShown.ToString(CultureInfo.InvariantCulture)).Append(" status file(s) not shown: the signature or the writer rule failed\n");
        }

        return sb.ToString();
    }

    /// <summary>The owner's waiting list (deliverable 18, "Waiting list"): one fixed line per item, or that nothing waits.</summary>
    internal static string RenderWaiting(IReadOnlyList<OwnerItem> items, DateTimeOffset now) =>
        items.Count == 0 ? "nothing waits for you\n" : string.Concat(items.Select(i => i.Describe(now) + "\n"));

    private static async Task<int> BoardAsync(CliEnvironment environment, VerbIo io, Action<IServiceCollection>? configure, ParseResult parseResult, bool waiting, CancellationToken ct)
    {
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
            BusInventory inventory = services.GetRequiredService<BusInventory>();
            if (waiting)
            {
                IReadOnlyList<OwnerItem> items = await inventory.WaitingAsync(configuration.Supervision.WorkMachines, ct);
                await io.Out.WriteAsync(RenderWaiting(items, services.GetRequiredService<TimeProvider>().GetUtcNow()));
                return ExitCodes.Ok;
            }

            WorkBoard board = await inventory.WorkAsync(configuration.Supervision.WorkMachines, ct);
            await io.Out.WriteAsync(Render(board));
            return ExitCodes.Ok;
        }
        catch (BusBusyException ex)
        {
            return BusHost.ConfigurationError(io, ex.Message);
        }
    }

    /// <summary>A hook mode: never stdout, never a non-zero exit; at most one stderr line.</summary>
    private static async Task<int> HookAsync(CliEnvironment environment, VerbIo io, Action<IServiceCollection>? configure, ParseResult parseResult, string mode, CancellationToken ct)
    {
        if (environment.Variables.TryGetValue("ZYGGY_HOOKS", out string? hooks) && hooks == "off")
        {
            return ExitCodes.Ok;
        }

        (NodeConfiguration? configuration, _) = BusHost.LoadConfiguration(parseResult, environment, io);
        if (configuration is null)
        {
            return ExitCodes.Ok;
        }

        if (configuration.SignWith is null)
        {
            await io.Error.WriteLineAsync("work: signWith missing in node.json");
            return ExitCodes.Ok;
        }

        string? patternsError = null;
        await using ServiceProvider services = BusHost.BuildServices(configuration, parseResult.GetValue(BusHost.VerboseOption), environment, s =>
        {
            patternsError = s.AddSecretPatterns(environment.Variables);
            configure?.Invoke(s);
        });
        if (patternsError is not null)
        {
            await io.Error.WriteLineAsync("work: " + patternsError);
            return ExitCodes.Ok;
        }

        if (await BusHost.CheckCheckoutAsync(services, configuration, io, ct) != ExitCodes.Ok)
        {
            return ExitCodes.Ok;
        }

        string? hookJson = io.InputRedirected ? await io.In.ReadToEndAsync(ct) : null;
        WorkStatusHook hook = services.GetRequiredService<WorkStatusHook>();
        WorkHookOutcome outcome = mode switch
        {
            "start" => await hook.SessionStartAsync(hookJson, ct),
            "turn" => await hook.TurnEndAsync(hookJson, ct),
            _ => await hook.SessionEndAsync(hookJson, ct),
        };
        string? line = outcome.Result switch
        {
            WorkHookResult.Unparseable => "work: unparseable hook input",
            WorkHookResult.Refused or WorkHookResult.RefusedBySecretPattern => "work: status refused (" + FirstLine(outcome.Detail) + ")",
            WorkHookResult.Failed => "work: status not " + (mode == "end" ? "removed" : "published") + " (" + FirstLine(outcome.Detail) + ")",
            _ => null,
        };
        if (line is not null)
        {
            await io.Error.WriteLineAsync(line);
        }

        if (mode == "start")
        {
            // The one thing a hook mode prints on stdout: a decision that came after the wait of a question asked in this project, once.
            foreach (DecisionRecord late in await services.GetRequiredService<AskFlow>().LateDecisionsAsync(hookJson, ct))
            {
                await io.Out.WriteAsync(
                    "zyggy: Central decided on question " + late.InReplyTo.Value + " (" + EnvelopeWire.ToWire(late.Fields.Kind) + "), after the wait ended:\n" + late.Text);
            }

            // Deliverable 18 ("Waiting list"): on Central, the session start also names how many items wait for the owner, when any do.
            if (configuration.Machine == MachineName.Central)
            {
                try
                {
                    int count = (await services.GetRequiredService<BusInventory>().WaitingAsync(configuration.Supervision.WorkMachines, ct)).Count;
                    if (count > 0)
                    {
                        await io.Out.WriteLineAsync(count.ToString(CultureInfo.InvariantCulture) + " item(s) wait for you: zyggy work --waiting");
                    }
                }
                catch (BusBusyException)
                {
                    // A busy checkout costs the count at this start, nothing more.
                }
            }
        }

        return ExitCodes.Ok;
    }

    /// <summary>The first non-empty line of a failure text: git answers in several lines, the hook prints one.</summary>
    private static string FirstLine(string? detail) =>
        (detail ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;

    private static async Task<int> StepAsync(CliEnvironment environment, VerbIo io, Action<IServiceCollection>? configure, ParseResult parseResult, string? session, string label)
    {
        (NodeConfiguration? configuration, int exitCode) = BusHost.LoadConfiguration(parseResult, environment, io);
        if (configuration is null)
        {
            return exitCode;
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

        WorkHookOutcome outcome = services.GetRequiredService<WorkStatusHook>().StateStep(session, label);
        switch (outcome.Result)
        {
            case WorkHookResult.Recorded:
                return ExitCodes.Ok;
            case WorkHookResult.RefusedBySecretPattern:
                await io.Error.WriteLineAsync("zyggy: refused: " + outcome.Detail);
                return ExitCodes.Refused;
            case WorkHookResult.Refused:
                await io.Error.WriteLineAsync("zyggy: " + outcome.Detail);
                return ExitCodes.Usage;
            default:
                await io.Error.WriteLineAsync("zyggy: step not recorded: " + outcome.Detail);
                return ExitCodes.Failed;
        }
    }
}
