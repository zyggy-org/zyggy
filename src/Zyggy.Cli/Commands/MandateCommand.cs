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
/// <c>zyggy mandate grant | show | revoke | resume</c> (deliverable 18, "The mandate"): the owner grants, revokes and resumes a mandate in an attended
/// session on Central; any machine shows them. Ordering, option reading and rendering only — every decision is in Core
/// (<see cref="MandateGranter"/>, <see cref="BusInventory.MandatesAsync"/>, <see cref="MandateLedger.StateAt"/>).
/// </summary>
internal static class MandateCommand
{
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    public static Command Create(CliEnvironment environment, VerbIo io, Action<IServiceCollection>? configure) =>
        new("mandate", "What the owner pre-approved Central to run on a machine: grant (Central, attended), show (anywhere), revoke and resume (Central, attended).")
        {
            CreateGrant(environment, io, configure),
            CreateShow(environment, io, configure),
            CreateRevoke(environment, io, configure),
            CreateResume(environment, io, configure),
        };

    /// <summary>The mandate as <c>zyggy mandate grant</c> prints it: every field, so the owner sees what was granted.</summary>
    internal static string RenderGrant(EnvelopeId id, MachineName to, MandateFields f)
    {
        var sb = new StringBuilder();
        sb.Append("granted ").Append(id.Value).Append("  central→").Append(to.Value).Append('\n')
            .Append("  project: ").Append(f.Project).Append('\n')
            .Append("  goal: ").Append(f.Goal).Append('\n');
        if (f.Spec is not null)
        {
            sb.Append("  spec: ").Append(f.Spec).Append(" at ").Append(f.SpecCommit).Append('\n');
        }

        sb.Append("  branches: ").Append(f.Branches.Count == 0 ? "(none)" : string.Join(", ", f.Branches)).Append('\n')
            .Append("  allowed_tools: ").AppendJoin(", ", f.AllowedTools).Append('\n')
            .Append("  max_jobs: ").Append(f.MaxJobs.ToString(CultureInfo.InvariantCulture))
            .Append("  per hour: ").Append(f.MaxJobsPerHour.ToString(CultureInfo.InvariantCulture))
            .Append("  per step: ").Append(f.MaxJobsPerStep.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append("  expires: ").Append(f.Expires.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture)).Append('\n')
            .Append("  grants: ").AppendJoin(", ", f.Grants.Select(EnvelopeWire.ToWire)).Append('\n')
            .Append("  sessions: ").Append(f.Sessions ? "true" : "false").Append('\n');
        return sb.ToString();
    }

    /// <summary>The list as <c>zyggy mandate show</c> prints it: one line per mandate with its state, jobs used and time left.</summary>
    internal static string RenderShow(IReadOnlyList<MandateLedger> ledgers, DateTimeOffset now)
    {
        if (ledgers.Count == 0)
        {
            return "no mandates\n";
        }

        var sb = new StringBuilder();
        foreach (MandateLedger ledger in ledgers)
        {
            MandateState state = ledger.StateAt(now);
            sb.Append(ledger.Id.Value).Append("  ");
            if (ledger.Mandate?.Mandate is not { } f)
            {
                sb.Append(StateWord(state)).Append('\n');
                continue;
            }

            sb.Append(ledger.Mandate.Header.To.Value).Append("  ").Append(f.Project).Append("  ").Append(StateWord(state))
                .Append("  jobs ").Append(ledger.Counted.Count.ToString(CultureInfo.InvariantCulture)).Append('/').Append(f.MaxJobs.ToString(CultureInfo.InvariantCulture))
                .Append("  ").Append(state is MandateState.Revoked or MandateState.Expired ? "no time left" : TimeLeft(f.Expires - now) + " left")
                .Append("  ").Append(f.Goal).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>Reads <c>--expires</c>: a UTC timestamp, or a duration from now in days (<c>3d</c>) or hours (<c>36h</c>).</summary>
    internal static bool TryParseExpires(string? text, DateTimeOffset now, out DateTimeOffset expires)
    {
        expires = default;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (text.Length >= 2 && (text[^1] == 'd' || text[^1] == 'h')
            && int.TryParse(text.AsSpan(0, text.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out int amount) && amount > 0 && amount <= 24 * 30)
        {
            expires = now + (text[^1] == 'd' ? TimeSpan.FromDays(amount) : TimeSpan.FromHours(amount));
            return true;
        }

        return DateTimeOffset.TryParseExact(text, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out expires);
    }

    private static Command CreateGrant(CliEnvironment environment, VerbIo io, Action<IServiceCollection>? configure)
    {
        var to = new Option<string>("--to") { Description = "The machine the mandate is for.", Required = true };
        var project = new Option<string>("--project") { Description = "The project's registry name on that machine.", Required = true };
        var goal = new Option<string>("--goal") { Description = "One line, 1-200 characters: what the mandate is for.", Required = true };
        var spec = new Option<string?>("--spec") { Description = "The approved spec, a repository-relative path (with --spec-commit)." };
        var specCommit = new Option<string?>("--spec-commit") { Description = "The 40-character commit at which the owner approved the spec." };
        var branch = new Option<string[]>("--branch") { Description = "A branch a job may start from and push; may be repeated up to 5 times.", Arity = ArgumentArity.ZeroOrMore };
        var allowedTools = new Option<string[]>("--allowed-tools") { Description = "The tool ceiling, comma-separated; may be repeated.", Required = true, Arity = ArgumentArity.OneOrMore };
        var maxJobs = new Option<int>("--max-jobs") { Description = "Total jobs under the mandate, 1-200.", Required = true };
        var perHour = new Option<int?>("--max-jobs-per-hour") { Description = "1-60 (6 when absent)." };
        var perStep = new Option<int?>("--max-jobs-per-step") { Description = "1-20 (4 when absent)." };
        var expires = new Option<string>("--expires") { Description = "A UTC timestamp (2026-11-20T18:00:00Z) or a duration (3d, 36h); at most 7 days ahead.", Required = true };
        var grant = new Option<string[]>("--grant") { Description = "accept, merge, release or deploy; may be repeated (accept when absent).", Arity = ArgumentArity.ZeroOrMore };
        var sessions = new Option<bool>("--sessions") { Description = "Tasks may be delivered into a live session." };
        var command = new Command("grant", "Central, attended only: sign and publish a mandate for one project on one machine.")
        {
            to, project, goal, spec, specCommit, branch, allowedTools, maxJobs, perHour, perStep, expires, grant, sessions, BusHost.ConfigOption, BusHost.VerboseOption,
        };
        command.SetAction((parseResult, ct) => GrantAsync(
            environment,
            io,
            configure,
            parseResult,
            new GrantGiven(
                parseResult.GetValue(to),
                parseResult.GetValue(project),
                parseResult.GetValue(goal),
                parseResult.GetValue(spec),
                parseResult.GetValue(specCommit),
                parseResult.GetValue(branch) ?? [],
                parseResult.GetValue(allowedTools) ?? [],
                parseResult.GetValue(maxJobs),
                parseResult.GetValue(perHour),
                parseResult.GetValue(perStep),
                parseResult.GetValue(expires),
                parseResult.GetValue(grant) ?? [],
                parseResult.GetValue(sessions)),
            ct));
        return command;
    }

    private static Command CreateShow(CliEnvironment environment, VerbIo io, Action<IServiceCollection>? configure)
    {
        var command = new Command("show", "List every mandate with its state, jobs used and time left.") { BusHost.ConfigOption, BusHost.VerboseOption };
        command.SetAction((parseResult, ct) => ShowAsync(environment, io, configure, parseResult, ct));
        return command;
    }

    private static Command CreateRevoke(CliEnvironment environment, VerbIo io, Action<IServiceCollection>? configure)
    {
        var id = new Argument<string>("id") { Description = "The mandate's id." };
        var command = new Command("revoke", "Central, attended only: move a mandate to mandates/revoked/; nothing more is submitted under it.") { id, BusHost.ConfigOption, BusHost.VerboseOption };
        command.SetAction((parseResult, ct) => RevokeAsync(environment, io, configure, parseResult, parseResult.GetValue(id), ct));
        return command;
    }

    private static Command CreateResume(CliEnvironment environment, VerbIo io, Action<IServiceCollection>? configure)
    {
        var id = new Argument<string>("id") { Description = "The mandate's id." };
        var command = new Command("resume", "Central, attended only: write a resume decision on a paused mandate; earlier reports no longer count towards a pause.") { id, BusHost.ConfigOption, BusHost.VerboseOption };
        command.SetAction((parseResult, ct) => ResumeAsync(environment, io, configure, parseResult, parseResult.GetValue(id), ct));
        return command;
    }

    private static async Task<int> GrantAsync(CliEnvironment environment, VerbIo io, Action<IServiceCollection>? configure, ParseResult parseResult, GrantGiven given, CancellationToken ct)
    {
        if (environment.HooksOff)
        {
            await io.Error.WriteLineAsync("zyggy: refused: a mandate is granted only in an attended session");
            return ExitCodes.Configuration;
        }

        if (!MachineName.TryParse(given.To, out MachineName? to))
        {
            await io.Error.WriteLineAsync("zyggy: mandate grant: --to must be a lowercase label ([a-z0-9-], 1-63)");
            return ExitCodes.Usage;
        }

        var grants = new List<MandateGrant>();
        foreach (string text in given.Grants)
        {
            if (!EnvelopeWire.TryFromWire(text, out MandateGrant g))
            {
                await io.Error.WriteLineAsync("zyggy: mandate grant: --grant must be accept, merge, release or deploy");
                return ExitCodes.Usage;
            }

            grants.Add(g);
        }

        (NodeConfiguration? configuration, int exitCode) = BusHost.LoadConfiguration(parseResult, environment, io);
        if (configuration is null)
        {
            return exitCode;
        }

        if (configuration.Machine != MachineName.Central)
        {
            await io.Error.WriteLineAsync("zyggy: refused: only central grants a mandate");
            return ExitCodes.Configuration;
        }

        if (configuration.SignWith is null)
        {
            return BusHost.ConfigurationError(io, "signWith missing");
        }

        await using ServiceProvider services = BusHost.BuildServices(configuration, parseResult.GetValue(BusHost.VerboseOption), environment, configure);
        if (!TryParseExpires(given.Expires, services.GetRequiredService<TimeProvider>().GetUtcNow(), out DateTimeOffset expires))
        {
            await io.Error.WriteLineAsync("zyggy: mandate grant: --expires must be a UTC timestamp (yyyy-MM-ddTHH:mm:ssZ) or a duration such as 3d or 36h");
            return ExitCodes.Usage;
        }

        int checkout = await BusHost.CheckCheckoutAsync(services, configuration, io, ct);
        if (checkout != ExitCodes.Ok)
        {
            return checkout;
        }

        string[] tools = [.. given.AllowedTools.SelectMany(t => t.Split(',')).Select(t => t.Trim())];
        var fields = new MandateFields(
            given.Project ?? string.Empty,
            given.Goal ?? string.Empty,
            tools,
            given.MaxJobs,
            expires,
            given.Spec,
            given.SpecCommit,
            given.Branches,
            given.PerHour ?? MandateFields.DefaultMaxJobsPerHour,
            given.PerStep ?? MandateFields.DefaultMaxJobsPerStep,
            grants.Count == 0 ? null : grants,
            given.Sessions);
        AskDecideResult result = await services.GetRequiredService<MandateGranter>().GrantAsync(to, fields, ct);
        switch (result)
        {
            case AskDecidePublished published:
                await io.Out.WriteAsync(RenderGrant(published.Id, to, fields));
                if (!published.Pushed)
                {
                    await io.Out.WriteLineAsync("(not pushed yet: the next bus write on this machine pushes it)");
                    return ExitCodes.PushDeferred;
                }

                return ExitCodes.Ok;
            case AskDecideRefused refused:
                await io.Error.WriteLineAsync("zyggy: refused: " + refused.Field + ": " + refused.Detail);
                return ExitCodes.Configuration;
            default:
                await io.Error.WriteLineAsync("zyggy: mandate not written: " + (result is AskDecideFailed failed ? failed.Stage + ": " + failed.Detail : result.GetType().Name));
                return ExitCodes.Failed;
        }
    }

    private static async Task<int> ShowAsync(CliEnvironment environment, VerbIo io, Action<IServiceCollection>? configure, ParseResult parseResult, CancellationToken ct)
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
            BusSyncResult pulled = await services.GetRequiredService<BusRepository>().PullAsync(ct);
            if (!pulled.Succeeded)
            {
                await io.Error.WriteLineAsync("zyggy: mandate show: bus not pulled, showing the checkout as it is");
            }

            IReadOnlyList<MandateLedger> ledgers = await services.GetRequiredService<BusInventory>().MandatesAsync(ct);
            await io.Out.WriteAsync(RenderShow(ledgers, services.GetRequiredService<TimeProvider>().GetUtcNow()));
            return ExitCodes.Ok;
        }
        catch (BusBusyException ex)
        {
            return BusHost.ConfigurationError(io, ex.Message);
        }
    }

    private static Task<int> RevokeAsync(CliEnvironment environment, VerbIo io, Action<IServiceCollection>? configure, ParseResult parseResult, string? idText, CancellationToken ct) =>
        ChangeAsync(environment, io, configure, parseResult, idText, "revoke", "revoked", withPatterns: false, (services, id, token) => services.GetRequiredService<MandateGranter>().RevokeAsync(id, token), ct);

    private static Task<int> ResumeAsync(CliEnvironment environment, VerbIo io, Action<IServiceCollection>? configure, ParseResult parseResult, string? idText, CancellationToken ct) =>
        ChangeAsync(
            environment,
            io,
            configure,
            parseResult,
            idText,
            "resume",
            "resumed",
            withPatterns: true,
            (services, id, token) => services.GetRequiredService<MandateGranter>().ResumeAsync(id, services.GetRequiredService<DecisionWriter>(), token),
            ct);

    // revoke and resume: attended, Central only, pull, then one Core call whose result is printed the same way.
    private static async Task<int> ChangeAsync(
        CliEnvironment environment,
        VerbIo io,
        Action<IServiceCollection>? configure,
        ParseResult parseResult,
        string? idText,
        string verb,
        string done,
        bool withPatterns,
        Func<IServiceProvider, EnvelopeId, CancellationToken, Task<AskDecideResult>> act,
        CancellationToken ct)
    {
        if (environment.HooksOff)
        {
            await io.Error.WriteLineAsync("zyggy: refused: a mandate is " + done + " only in an attended session");
            return ExitCodes.Configuration;
        }

        if (!EnvelopeId.TryParse(idText, out EnvelopeId? id))
        {
            await io.Error.WriteLineAsync("zyggy: mandate " + verb + ": the id must be a ULID");
            return ExitCodes.Usage;
        }

        (NodeConfiguration? configuration, int exitCode) = BusHost.LoadConfiguration(parseResult, environment, io);
        if (configuration is null)
        {
            return exitCode;
        }

        if (configuration.Machine != MachineName.Central)
        {
            await io.Error.WriteLineAsync("zyggy: refused: only central " + verb + "s a mandate");
            return ExitCodes.Configuration;
        }

        string? patternsError = null;
        await using ServiceProvider services = BusHost.BuildServices(configuration, parseResult.GetValue(BusHost.VerboseOption), environment, s =>
        {
            if (withPatterns)
            {
                patternsError = s.AddSecretPatterns(environment.Variables);
            }

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

        // The mandate may have reached the remote a moment ago; a failed pull leaves the checkout as it was and the lookup says so.
        await services.GetRequiredService<BusRepository>().PullAsync(ct);
        AskDecideResult result = await act(services, id, ct);
        switch (result)
        {
            case AskDecidePublished { Pushed: true } published:
                await io.Out.WriteLineAsync(done + " " + published.Id.Value);
                return ExitCodes.Ok;
            case AskDecidePublished deferred:
                await io.Out.WriteLineAsync(done + " " + deferred.Id.Value + "  (not pushed yet)");
                return ExitCodes.PushDeferred;
            case AskDecideNotFound notFound:
                await io.Error.WriteLineAsync("zyggy: mandate " + verb + ": " + notFound.Detail);
                return ExitCodes.NotFound;
            case AskDecideRefused refused:
                await io.Error.WriteLineAsync("zyggy: refused: " + refused.Field + ": " + refused.Detail);
                return ExitCodes.Configuration;
            default:
                await io.Error.WriteLineAsync("zyggy: mandate not " + done + ": " + (result is AskDecideFailed failed ? failed.Stage + ": " + failed.Detail : result.GetType().Name));
                return ExitCodes.Failed;
        }
    }

    private static string StateWord(MandateState state) => state switch
    {
        MandateState.Absent => "absent",
        MandateState.Unverified => "unverified",
        MandateState.Revoked => "revoked",
        MandateState.Expired => "expired",
        MandateState.Paused => "paused",
        MandateState.Exhausted => "exhausted",
        _ => "active",
    };

    private static string TimeLeft(TimeSpan left) =>
        left.TotalDays >= 1
            ? ((int)left.TotalDays).ToString(CultureInfo.InvariantCulture) + "d " + left.Hours.ToString(CultureInfo.InvariantCulture) + "h"
            : left.Hours.ToString(CultureInfo.InvariantCulture) + "h " + left.Minutes.ToString(CultureInfo.InvariantCulture) + "m";

    private sealed record GrantGiven(
        string? To,
        string? Project,
        string? Goal,
        string? Spec,
        string? SpecCommit,
        string[] Branches,
        string[] AllowedTools,
        int MaxJobs,
        int? PerHour,
        int? PerStep,
        string? Expires,
        string[] Grants,
        bool Sessions);
}
