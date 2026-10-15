using System.CommandLine;
using System.Globalization;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Zyggy.Core.Bus;
using Zyggy.Core.Bus.Probe;
using Zyggy.Core.Configuration;
using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;

namespace Zyggy.Cli.Commands;

/// <summary>
/// <c>zyggy probe …</c> (spec 04 CLI surface): the transport probe. <c>listen</c> runs the receiver's loop with the probe
/// handler until Ctrl+C; <c>send</c> pushes probes and waits for their reports; <c>stats</c> aggregates the sender's ledger;
/// <c>reach</c> follows in a later step.
/// </summary>
internal static class ProbeCommand
{
    private static readonly Option<string?> LedgerOption = new("--ledger") { Description = "The sender's ledger file (default: <LocalApplicationData>/zyggy/probe/<tenant>/sent.jsonl)." };

    public static Command Create(CliEnvironment environment)
    {
        var listen = new Command("listen", "Poll the bus and answer every probe job addressed to this machine until Ctrl+C.") { BusHost.ConfigOption, BusHost.VerboseOption };
        listen.SetAction((parseResult, ct) => ListenAsync(environment, parseResult, ct));

        var to = new Option<string>("--to") { Description = "The target machine.", Required = true };
        var count = new Option<int>("--count") { Description = "How many probes (default 1).", DefaultValueFactory = _ => 1 };
        var interval = new Option<int>("--interval") { Description = "Seconds between probes (default 30).", DefaultValueFactory = _ => 30 };
        var timeout = new Option<int>("--timeout") { Description = "Seconds to wait for each report (default 120).", DefaultValueFactory = _ => 120 };
        var send = new Command("send", "Push signed probe jobs to a machine and wait for its reports; the outcomes go to the ledger.") { to, count, interval, timeout, LedgerOption, BusHost.ConfigOption, BusHost.VerboseOption };
        send.SetAction((parseResult, ct) => SendAsync(environment, parseResult, parseResult.GetValue(to)!, parseResult.GetValue(count), parseResult.GetValue(interval), parseResult.GetValue(timeout), ct));

        var statsTo = new Option<string?>("--to") { Description = "Only this target machine." };
        var stats = new Command("stats", "Aggregate the sender's ledger per target machine as a Markdown table.") { statsTo, LedgerOption, BusHost.ConfigOption };
        stats.SetAction((parseResult, _) => Task.FromResult(Stats(environment, parseResult, parseResult.GetValue(statsTo))));

        var reach = new Command("reach", "Check what this machine can reach: the API, git fetch and push over HTTPS, SSH; nothing is written to the remote.") { BusHost.ConfigOption, BusHost.VerboseOption };
        reach.SetAction((parseResult, ct) => ReachAsync(environment, parseResult, ct));

        return new Command("probe", "The transport probe: send, listen, stats, reach.") { listen, send, stats, reach };
    }

    private static async Task<int> ReachAsync(CliEnvironment environment, ParseResult parseResult, CancellationToken ct)
    {
        (NodeConfiguration? configuration, int exitCode) = BusHost.LoadConfiguration(parseResult, environment);
        if (configuration is null)
        {
            return exitCode;
        }

        await using ServiceProvider services = BusHost.BuildServices(configuration, parseResult.GetValue(BusHost.VerboseOption), environment);
        int checkout = await CheckCheckoutAsync(services, configuration, ct);
        if (checkout != ExitCodes.Ok)
        {
            return checkout;
        }

        ReachReport report = await services.GetRequiredService<ReachabilityCheck>().RunAsync(ct);
        Console.Out.Write(report.RenderMarkdown());
        return report.ExitCode;
    }

    private static async Task<int> ListenAsync(CliEnvironment environment, ParseResult parseResult, CancellationToken ct)
    {
        (NodeConfiguration? configuration, int exitCode) = BusHost.LoadConfiguration(parseResult, environment);
        if (configuration is null)
        {
            return exitCode;
        }

        if (configuration.SignWith is null)
        {
            return BusHost.ConfigurationError("signWith missing");
        }

        await using ServiceProvider services = BusHost.BuildServices(
            configuration,
            parseResult.GetValue(BusHost.VerboseOption),
            environment,
            s => s.AddSingleton(ProbeHandler.For(configuration.Machine, TimeProvider.System)));

        // AC-27: configuration-shaped conditions exit 3 before anything is stashed or pulled.
        int checkout = await CheckCheckoutAsync(services, configuration, ct);
        if (checkout != ExitCodes.Ok)
        {
            return checkout;
        }

        using BusListener listener = services.GetRequiredService<BusListener>();
        BusStartupResult start = await listener.StartAsync(ct);
        switch (start)
        {
            case BusStartupResult.Clean:
            case BusStartupResult.Stashed:
                break;
            case BusStartupResult.ListenerRunning:
                Console.Error.WriteLine("zyggy: another zyggy probe listen already runs in this checkout (" + configuration.Bus.LocalPath + "); stop it first");
                return ExitCodes.Failed;
            case BusStartupResult.KeyMissing:
                Console.Error.WriteLine($"zyggy: signing key {configuration.SignWith} is not in the secret store; run: zyggy secret set {configuration.SignWith.SecretName} --hex");
                return ExitCodes.Failed;
            case BusStartupResult.RebaseInProgress:
                Console.Error.WriteLine("zyggy: a rebase or merge is in progress in bus.localPath");
                return ExitCodes.Failed;
            case BusStartupResult.NoTenantDirectory:
                Console.Error.WriteLine($"zyggy: no {BusPaths.Tenant(configuration.Tenant)}/ directory in bus.localPath");
                return ExitCodes.Failed;
            default:
                Console.Error.WriteLine("zyggy: the bus checkout refused to start: " + start);
                return ExitCodes.Failed;
        }

        Console.Error.WriteLine($"zyggy: listening as {configuration.Tenant}/{configuration.Machine} on {configuration.Bus.Owner}/{configuration.Bus.Repo} ({configuration.Bus.Branch}); Ctrl+C to stop");
        await listener.RunAsync(PollMode.Active, ct);
        return ExitCodes.Ok;
    }

    private static async Task<int> SendAsync(CliEnvironment environment, ParseResult parseResult, string toText, int count, int intervalSeconds, int timeoutSeconds, CancellationToken ct)
    {
        (NodeConfiguration? configuration, int exitCode) = BusHost.LoadConfiguration(parseResult, environment);
        if (configuration is null)
        {
            return exitCode;
        }

        if (configuration.SignWith is null)
        {
            return BusHost.ConfigurationError("signWith missing");
        }

        if (!MachineName.TryParse(toText, out MachineName? to))
        {
            Console.Error.WriteLine("zyggy: --to must be a lowercase label");
            return ExitCodes.Usage;
        }

        if (count < 1 || intervalSeconds < 0 || timeoutSeconds < 1)
        {
            Console.Error.WriteLine("zyggy: --count and --timeout must be positive, --interval non-negative");
            return ExitCodes.Usage;
        }

        await using ServiceProvider services = BusHost.BuildServices(configuration, parseResult.GetValue(BusHost.VerboseOption), environment);
        int checkout = await CheckCheckoutAsync(services, configuration, ct);
        if (checkout != ExitCodes.Ok)
        {
            return checkout;
        }

        var sender = new ProbeSender(
            services.GetRequiredService<IBusProvider>(),
            services.GetRequiredService<BusRepository>(),
            services.GetRequiredService<EnvelopeSigner>(),
            new BusIdentity(configuration.Tenant, configuration.Machine),
            configuration.SignWith,
            configuration.Poll.ToPollOptions(),
            TimeProvider.System,
            Random.Shared,
            new ProbeLedger(LedgerPath(parseResult, configuration.Tenant)),
            services.GetRequiredService<ILoggerFactory>().CreateLogger<ProbeSender>());
        IReadOnlyList<ProbeRecord> records = await sender.SendManyAsync(
            to,
            count,
            TimeSpan.FromSeconds(intervalSeconds),
            TimeSpan.FromSeconds(timeoutSeconds),
            record => Console.Out.WriteLine(Line(record)),
            ct);
        Console.Out.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"sent {records.Count}, done {records.Count(r => r.Outcome == ProbeOutcome.Done)}, rejected {records.Count(r => r.Outcome == ProbeOutcome.Rejected)}, timeout {records.Count(r => r.Outcome == ProbeOutcome.Timeout)}, push_failed {records.Count(r => r.Outcome == ProbeOutcome.PushFailed)}"));
        return records.All(r => r.Outcome == ProbeOutcome.Done) ? ExitCodes.Ok : ExitCodes.Failed;
    }

    private static int Stats(CliEnvironment environment, ParseResult parseResult, string? toText)
    {
        (NodeConfiguration? configuration, int exitCode) = BusHost.LoadConfiguration(parseResult, environment);
        if (configuration is null)
        {
            return exitCode;
        }

        MachineName? to = null;
        if (toText is not null && !MachineName.TryParse(toText, out to))
        {
            Console.Error.WriteLine("zyggy: --to must be a lowercase label");
            return ExitCodes.Usage;
        }

        IReadOnlyList<ProbeStatsRow> rows = ProbeStats.Aggregate(new ProbeLedger(LedgerPath(parseResult, configuration.Tenant)).ReadAll(), to);
        if (rows.Count == 0)
        {
            Console.Out.WriteLine("no probes");
            return ExitCodes.NoRun;
        }

        Console.Out.Write(ProbeStats.RenderMarkdown(rows));
        return ExitCodes.Ok;
    }

    private static string Line(ProbeRecord record) => record.Outcome switch
    {
        ProbeOutcome.Done => string.Create(CultureInfo.InvariantCulture, $"{record.Id} {record.To} done {record.RoundTripMs} ms"),
        ProbeOutcome.Rejected => $"{record.Id} {record.To} rejected {record.Reason}",
        ProbeOutcome.Timeout => $"{record.Id} {record.To} timeout",
        _ => $"{record.Id} {record.To} push_failed {record.Reason}",
    };

    private static string LedgerPath(ParseResult parseResult, TenantId tenant) =>
        parseResult.GetValue(LedgerOption) is { Length: > 0 } ledger
            ? ledger
            : ProbeLedger.DefaultPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), tenant);

    /// <summary>The checkout must exist, be a repository and sit on the bus branch (exit 3, one line); nothing is touched.</summary>
    private static async Task<int> CheckCheckoutAsync(ServiceProvider services, NodeConfiguration configuration, CancellationToken ct)
    {
        string localPath = configuration.Bus.LocalPath;
        if (!Directory.Exists(localPath) || !Directory.Exists(Path.Join(localPath, ".git")))
        {
            return BusHost.ConfigurationError("bus.localPath is not a git repository");
        }

        string? branch = await services.GetRequiredService<BusRepository>().CheckoutBranchAsync(ct);
        if (branch is null)
        {
            return BusHost.ConfigurationError("bus.localPath is not a git repository");
        }

        if (!string.Equals(branch, configuration.Bus.Branch, StringComparison.Ordinal))
        {
            return BusHost.ConfigurationError($"checkout is on '{branch}', expected '{configuration.Bus.Branch}'");
        }

        return ExitCodes.Ok;
    }
}
