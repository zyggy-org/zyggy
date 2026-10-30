using System.CommandLine;
using System.Globalization;
using System.Text;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Envelope;
using Zyggy.Core.Health;
using Zyggy.Core.Verbs;

namespace Zyggy.Cli.Commands;

/// <summary>
/// <c>zyggy status</c> (founding spec §3, §11; spec 08 AC-9/AC-10): the node block from the loopback health endpoint through
/// <see cref="HealthRules"/> (the one place "green" is defined), the bus block from <see cref="BusInventory"/>, or with <c>--id</c> the
/// trace of one envelope. Rendering only; every number and verdict comes from Core. Read-only: no pull, no lock but the read lock
/// the inventory takes, no stdin.
/// </summary>
internal static partial class StatusCommand
{
    /// <summary>The loopback-only test hook for the health URL (spec 08 Configuration): never a production override.</summary>
    private const string HealthUrlVariable = "ZYGGY_HEALTH_URL";

    private const int RecentReports = 10;

    private const string Dot = " · ";

    public static Command Create(CliEnvironment environment, VerbIo io, Action<IServiceCollection>? configure)
    {
        var id = new Option<string?>("--id") { Description = "Trace one envelope by its ULID instead of showing the node and the bus." };
        var command = new Command("status", "Show this node's health and the state of the tenant's bus, or trace one job.") { id, BusHost.ConfigOption, BusHost.VerboseOption };
        command.SetAction((parseResult, ct) => RunAsync(environment, io, configure, parseResult, parseResult.GetValue(id), ct));
        return command;
    }

    private static async Task<int> RunAsync(CliEnvironment environment, VerbIo io, Action<IServiceCollection>? configure, ParseResult parseResult, string? idText, CancellationToken ct)
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

        TimeProvider clock = services.GetRequiredService<TimeProvider>();
        BusInventory inventory = services.GetRequiredService<BusInventory>();
        try
        {
            return idText is not null
                ? await TraceAsync(io, configuration, inventory, clock, idText, ct)
                : await OverviewAsync(environment, io, configuration, services, inventory, clock, ct);
        }
        catch (BusBusyException ex)
        {
            return BusHost.ConfigurationError(io, ex.Message);
        }
    }

    private static async Task<int> OverviewAsync(CliEnvironment environment, VerbIo io, NodeConfiguration configuration, ServiceProvider services, BusInventory inventory, TimeProvider clock, CancellationToken ct)
    {
        Uri url;
        try
        {
            url = environment.Get(HealthUrlVariable) is { } text ? new Uri(text) : HealthClient.DefaultUrl;
            using var client = new HealthClient(url, clock);
            HealthProbe probe = await client.ReadAsync(ct);
            DateTimeOffset now = clock.GetUtcNow();
            NodeHealth? health = (probe as Reachable)?.Health;
            HealthVerdict verdict = HealthRules.Evaluate(health, now, services.GetRequiredService<PollOptions>(), BusHost.InformationalVersion(), url.ToString());
            if (services.GetService<ILoggerFactory>() is { } loggers)
            {
                string level = verdict.Level.ToString();
                ILogger logger = loggers.CreateLogger(typeof(StatusCommand).FullName!);
                LogHealth(logger, level);
            }

            InventoryReport report = await inventory.ListAsync(RecentReports, ct);
            await io.Out.WriteAsync(RenderOverview(configuration, health, verdict, report, now));
            return verdict.Level switch
            {
                HealthLevel.Green => ExitCodes.Ok,
                HealthLevel.Amber => ExitCodes.Amber,
                _ => ExitCodes.Failed,
            };
        }
        catch (ArgumentException)
        {
            return BusHost.ConfigurationError(io, "health url must be loopback");
        }
        catch (UriFormatException)
        {
            return BusHost.ConfigurationError(io, "health url must be loopback");
        }
    }

    private static async Task<int> TraceAsync(VerbIo io, NodeConfiguration configuration, BusInventory inventory, TimeProvider clock, string idText, CancellationToken ct)
    {
        if (!EnvelopeId.TryParse(idText, out EnvelopeId? id))
        {
            await io.Error.WriteLineAsync("zyggy: --id must be a ULID");
            return ExitCodes.Usage;
        }

        EnvelopeTrace? trace = await inventory.TraceAsync(id, ct);
        if (trace is null)
        {
            await io.Out.WriteLineAsync("not found in tenant " + configuration.Tenant.Value);
            return ExitCodes.NotFound;
        }

        await io.Out.WriteAsync(RenderTrace(trace, clock.GetUtcNow()));
        return ExitCodes.Ok;
    }

    /// <summary>The spec's "Output formats" block for <c>zyggy status</c>, line by line.</summary>
    internal static string RenderOverview(NodeConfiguration configuration, NodeHealth? health, HealthVerdict verdict, InventoryReport report, DateTimeOffset now)
    {
        var sb = new StringBuilder();
        sb.Append("node    ").Append(configuration.Machine.Value).Append(" (").Append(configuration.Tenant.Value).Append(')');
        if (health is not null)
        {
            sb.Append(Dot).Append("zyggy-node ").Append(health.Version);
        }

        sb.Append(Dot).Append("health: ").Append(verdict.Level.ToString().ToLowerInvariant());
        if (verdict.Reasons.Count > 0)
        {
            sb.Append(" — ").Append(string.Join("; ", verdict.Reasons));
        }

        sb.Append('\n');
        if (health is not null)
        {
            sb.Append("        last poll ").Append(AgeOrNever(now, health.LastPollUtc))
                .Append(Dot).Append("last pull ").Append(AgeOrNever(now, health.LastPullUtc))
                .Append(Dot).Append("head ").Append(Short(health.BusHeadSha))
                .Append(Dot).Append("running ").Append(health.JobsRunning.ToString(CultureInfo.InvariantCulture))
                .Append(Dot).Append("claimed ").Append(health.JobsClaimedByMe.ToString(CultureInfo.InvariantCulture))
                .Append(Dot).Append("last report ").Append(health.LastReportUtc is { } lr ? Timestamp(lr) : "never")
                .Append(Dot).Append("claude auth ").Append(health.ClaudeAuthOk switch { true => "ok", false => "expired", null => "not probed" })
                .Append('\n');
        }

        int tenants = report.TenantDirectories.Count;
        sb.Append("bus     head ").Append(Short(report.HeadSha)).Append(Dot)
            .Append(tenants.ToString(CultureInfo.InvariantCulture)).Append(tenants == 1 ? " tenant directory" : " tenant directories").Append('\n');
        sb.Append("        machine       queued  claimed  rejected  reports  context\n");
        foreach (MachineCounts m in report.Machines)
        {
            sb.Append("        ").Append(m.Machine.Value.PadRight(14))
                .Append(m.Queued.ToString(CultureInfo.InvariantCulture).PadLeft(6))
                .Append(m.Claimed.ToString(CultureInfo.InvariantCulture).PadLeft(9))
                .Append(m.Rejected.ToString(CultureInfo.InvariantCulture).PadLeft(10))
                .Append(m.Reports.ToString(CultureInfo.InvariantCulture).PadLeft(9))
                .Append(m.Context.ToString(CultureInfo.InvariantCulture).PadLeft(9))
                .Append('\n');
        }

        int total = report.ReportsTotal;
        sb.Append("recent reports (").Append(report.RecentReports.Count.ToString(CultureInfo.InvariantCulture)).Append(" of ").Append(total.ToString(CultureInfo.InvariantCulture)).Append(")\n");
        foreach (EnvelopeSummary r in report.RecentReports)
        {
            sb.Append("        ").Append(r.Id.Value).Append("  ").Append(Route(r)).Append("  ");
            if (r.Verified)
            {
                sb.Append(StatusText(r));
                if (r.InReplyTo is { } reply)
                {
                    sb.Append("  in_reply_to ").Append(reply.Value);
                }

                if (r.CostUsd is { } cost)
                {
                    sb.Append("  cost_usd ").Append(cost.ToString(CultureInfo.InvariantCulture));
                }
            }
            else
            {
                sb.Append("(unverified: ").Append(r.Problem).Append(')');
            }

            sb.Append("  ").Append(r.Created is { } created ? HealthRules.Age(now, created) : "age unknown").Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>The spec's "Output formats" block for <c>zyggy status --id</c>: the job line, the state line, every report line, then the body verbatim as data.</summary>
    internal static string RenderTrace(EnvelopeTrace trace, DateTimeOffset now)
    {
        var sb = new StringBuilder();
        sb.Append("job     ").Append(trace.Id.Value).Append("  ");
        if (trace.Job is null)
        {
            sb.Append("(file gone)");
        }
        else if (!trace.Job.Verified)
        {
            sb.Append("(unverified: ").Append(trace.Job.Problem).Append(')');
        }
        else
        {
            sb.Append(Route(trace.Job));
            if (trace.Job.Project is { } project)
            {
                sb.Append("  project ").Append(project);
            }

            if (trace.Job.Priority is { } priority)
            {
                sb.Append("  priority ").Append(EnvelopeWire.ToWire(priority));
            }

            if (trace.Job.Created is { } created)
            {
                sb.Append("  created ").Append(Timestamp(created));
            }
        }

        sb.Append('\n');
        string machine = trace.Machine?.Value ?? "unknown";
        sb.Append("state   ").Append(trace.State switch
        {
            TraceState.Queued => "queued at " + machine + " since " + AgeOrNever(now, trace.Job?.Created),
            TraceState.Claimed => "claimed by " + machine + " since " + AgeOrNever(now, trace.Job?.Created),
            TraceState.Rejected => "rejected by " + machine + ": " + (trace.Reports.LastOrDefault(r => r.Verified && r.Status == ReportStatus.Rejected)?.Reason ?? trace.Job?.Problem ?? "unknown"),
            TraceState.Reported => "reported by " + machine,
            _ => "unknown",
        }).Append('\n');
        foreach (EnvelopeSummary r in trace.Reports)
        {
            sb.Append("report  ").Append(r.Id.Value).Append("  ");
            if (r.Verified)
            {
                sb.Append(StatusText(r));
                if (r.CostUsd is { } cost)
                {
                    sb.Append("  cost_usd ").Append(cost.ToString(CultureInfo.InvariantCulture));
                }

                if (r.Created is { } created)
                {
                    sb.Append("  created ").Append(Timestamp(created));
                }
            }
            else
            {
                sb.Append("(unverified: ").Append(r.Problem).Append(')');
            }

            sb.Append('\n');
        }

        if (trace.ReportBody is { } body)
        {
            sb.Append("body:\n").Append(body);
        }

        return sb.ToString();
    }

    private static string Route(EnvelopeSummary e) => (e.From?.Value ?? "?") + "→" + (e.To?.Value ?? "?");

    private static string StatusText(EnvelopeSummary r) =>
        r.Status is { } status
            ? EnvelopeWire.ToWire(status) + (r.Reason is { } reason ? " " + reason : string.Empty)
            : r.Type is { } type ? EnvelopeWire.ToWire(type) : "?";

    private static string AgeOrNever(DateTimeOffset now, DateTimeOffset? then) => then is { } t ? HealthRules.Age(now, t) : "never";

    private static string Timestamp(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string Short(string? sha) => sha is null ? "unknown" : sha.Length > 12 ? sha[..12] : sha;

    [LoggerMessage(Level = LogLevel.Information, Message = "status: health {Level}")]
    private static partial void LogHealth(ILogger logger, string level);
}
