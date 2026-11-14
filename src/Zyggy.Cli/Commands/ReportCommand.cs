using System.CommandLine;
using System.Globalization;
using System.Text;

using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Envelope;
using Zyggy.Core.Verbs;

namespace Zyggy.Cli.Commands;

/// <summary>
/// <c>zyggy report &lt;id&gt;</c> (founding spec §3; spec 15 CLI): the newest verified report answering a job, from the local bus checkout —
/// the report part of <c>zyggy status --id</c> (spec 08 Q6) with every result field. The trace is <see cref="BusInventory"/>'s; this verb
/// only renders it. The body is printed verbatim as data, never interpreted. Read-only: no pull, never the listen lock.
/// </summary>
internal static class ReportCommand
{
    private const string None = "-";

    public static Command Create(CliEnvironment environment, VerbIo io, Action<IServiceCollection>? configure)
    {
        var id = new Argument<string>("id") { Description = "The job's ULID." };
        var command = new Command("report", "Print the newest verified report answering a job, its fields and its body.") { id, BusHost.ConfigOption, BusHost.VerboseOption };
        command.SetAction((parseResult, ct) => RunAsync(environment, io, configure, parseResult, parseResult.GetValue(id)!, ct));
        return command;
    }

    private static async Task<int> RunAsync(CliEnvironment environment, VerbIo io, Action<IServiceCollection>? configure, ParseResult parseResult, string idText, CancellationToken ct)
    {
        if (!EnvelopeId.TryParse(idText, out EnvelopeId? id))
        {
            await io.Error.WriteLineAsync("zyggy: report: <id> must be a ULID");
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

        EnvelopeTrace? trace;
        try
        {
            trace = await services.GetRequiredService<BusInventory>().TraceAsync(id, ct);
        }
        catch (BusBusyException ex)
        {
            return BusHost.ConfigurationError(io, ex.Message);
        }

        if (trace?.NewestReport is not { Report: { } fields } report)
        {
            await io.Out.WriteLineAsync($"no report for {id.Value} in tenant {configuration.Tenant.Value} (yet)");
            return ExitCodes.NotFound;
        }

        await io.Out.WriteAsync(Render(report, fields));
        return ExitCodes.Ok;
    }

    /// <summary>The field lines in the spec's order (<c>reason</c> only when set, <c>-</c> for an absent value), then <c>body:</c> and the body verbatim.</summary>
    internal static string Render(Envelope report, ReportFields fields)
    {
        var sb = new StringBuilder();
        sb.Append("status: ").Append(EnvelopeWire.ToWire(fields.Status)).Append('\n');
        if (fields.Reason is { } reason)
        {
            sb.Append("reason: ").Append(reason).Append('\n');
        }

        sb.Append("files_changed: ").Append(fields.FilesChanged is { Count: > 0 } files ? string.Join(", ", files) : None).Append('\n');
        sb.Append("diff_ref: ").Append(fields.DiffRef ?? None).Append('\n');
        sb.Append("cost_usd: ").Append(fields.CostUsd is { } cost ? cost.ToString(CultureInfo.InvariantCulture) : None).Append('\n');
        sb.Append("from: ").Append(report.Header.From.Value).Append('\n');
        sb.Append("finished: ").Append(fields.Finished is { } finished ? finished.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) : None).Append('\n');
        sb.Append("body:\n").Append(report.BodyText);
        return sb.ToString();
    }
}
