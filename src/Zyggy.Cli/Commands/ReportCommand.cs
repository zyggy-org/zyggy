using System.CommandLine;
using System.Globalization;
using System.Security.Cryptography;
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

    /// <summary>Spec 59 slice 9: <c>--timeout</c> in minutes, 1 to a day; 30 when not given.</summary>
    private const int DefaultTimeoutMinutes = 30;
    private const int MaxTimeoutMinutes = 1440;

    /// <summary>How often a wait pulls the bus and looks again; the clock is the host's <see cref="TimeProvider"/>.</summary>
    private static readonly TimeSpan PullEvery = TimeSpan.FromSeconds(20);

    public static Command Create(CliEnvironment environment, VerbIo io, Action<IServiceCollection>? configure)
    {
        var id = new Argument<string>("id") { Description = "The job's ULID." };
        var file = new Option<string?>("--file")
        {
            Description = "Write the report's attached file (spec 59) to <out>, or to its file_name in the current folder; checked against the signed file_sha256 first.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var wait = new Option<bool>("--wait") { Description = "Pull the bus and look again until a report answers the job (exit 0, whatever its status) or the timeout passes (exit 3, 'no report yet')." };
        var timeout = new Option<string?>("--timeout") { Description = $"Minutes to wait with --wait, 1-{MaxTimeoutMinutes} (default {DefaultTimeoutMinutes})." };
        var command = new Command("report", "Print the newest verified report answering a job, its fields and its body.") { id, file, wait, timeout, BusHost.ConfigOption, BusHost.VerboseOption };
        command.SetAction((parseResult, ct) => RunAsync(
            environment,
            io,
            configure,
            parseResult,
            parseResult.GetValue(id)!,
            parseResult.GetResult(file) is null ? null : new FileWanted(parseResult.GetValue(file)),
            parseResult.GetValue(wait),
            parseResult.GetValue(timeout),
            ct));
        return command;
    }

    private static async Task<int> RunAsync(CliEnvironment environment, VerbIo io, Action<IServiceCollection>? configure, ParseResult parseResult, string idText, FileWanted? file, bool wait, string? timeoutText, CancellationToken ct)
    {
        if (!EnvelopeId.TryParse(idText, out EnvelopeId? id))
        {
            await io.Error.WriteLineAsync("zyggy: report: <id> must be a ULID");
            return ExitCodes.Usage;
        }

        if (timeoutText is not null && !wait)
        {
            await io.Error.WriteLineAsync("zyggy: report: --timeout needs --wait");
            return ExitCodes.Usage;
        }

        int timeoutMinutes = DefaultTimeoutMinutes;
        if (timeoutText is not null && (!int.TryParse(timeoutText, NumberStyles.None, CultureInfo.InvariantCulture, out timeoutMinutes) || timeoutMinutes is < 1 or > MaxTimeoutMinutes))
        {
            await io.Error.WriteLineAsync($"zyggy: report: --timeout must be 1-{MaxTimeoutMinutes} minutes");
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
            trace = wait
                ? await WaitForReportAsync(services, id, TimeSpan.FromMinutes(timeoutMinutes), ct)
                : await services.GetRequiredService<BusInventory>().TraceAsync(id, ct);
        }
        catch (BusBusyException ex)
        {
            return BusHost.ConfigurationError(io, ex.Message);
        }

        if (trace?.NewestReport is not { Report: { } fields } report)
        {
            if (wait && trace is not null && IsRejectedWithoutReport(trace))
            {
                await io.Out.WriteLineAsync($"job {id.Value} rejected without a report (its sender could not be read)");
                return ExitCodes.NoReportYet;
            }

            if (wait)
            {
                await io.Out.WriteLineAsync($"no report yet for {id.Value} in tenant {configuration.Tenant.Value} after {timeoutMinutes} min");
                return ExitCodes.NoReportYet;
            }

            await io.Out.WriteLineAsync($"no report for {id.Value} in tenant {configuration.Tenant.Value} (yet)");
            return ExitCodes.NotFound;
        }

        if (file is not null)
        {
            return await WriteAttachmentAsync(io, configuration, trace, fields, file, ct);
        }

        await io.Out.WriteAsync(Render(report, fields));
        return ExitCodes.Ok;
    }

    // Spec 59 (Q2, AC-3): the attached file beside the newest verified report, written only when its SHA-256 equals the signed file_sha256.
    private static async Task<int> WriteAttachmentAsync(VerbIo io, NodeConfiguration configuration, EnvelopeTrace trace, ReportFields fields, FileWanted file, CancellationToken ct)
    {
        EnvelopeSummary? newest = trace.Reports.LastOrDefault(r => r.Verified);
        if (fields.FileName is null || fields.FileSha256 is null || newest is null)
        {
            await io.Error.WriteLineAsync($"zyggy: report {trace.Id.Value} has no attached file");
            return ExitCodes.NotFound;
        }

        string source = BusPaths.Resolve(configuration.Bus.LocalPath, BusPaths.AttachmentFile(new BusFile(newest.RelativePath, newest.Id)));
        if (!File.Exists(source))
        {
            await io.Error.WriteLineAsync($"zyggy: the attached file of report {trace.Id.Value} is not in the bus checkout");
            return ExitCodes.NotFound;
        }

        byte[] bytes = await File.ReadAllBytesAsync(source, ct);
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), fields.FileSha256, StringComparison.Ordinal))
        {
            await io.Error.WriteLineAsync($"zyggy: refused: the attached file of report {trace.Id.Value} does not match its signed file_sha256; nothing written");
            return ExitCodes.Failed;
        }

        // file_name is data from the bus: only its last segment is used, never a path it names.
        string target = Path.GetFullPath(file.Out ?? Path.GetFileName(fields.FileName));
        await File.WriteAllBytesAsync(target, bytes, ct);
        await io.Out.WriteLineAsync($"wrote {bytes.Length} bytes to {target}");
        return ExitCodes.Ok;
    }

    /// <summary>
    /// Spec 59 slice 9 (AC-12): pull the bus and trace the job until a verified report answers it — any terminal status, since a
    /// failed, timed-out or rejected report ends the job as surely as a done one — or <paramref name="timeout"/> passes on the host's
    /// clock. Each pull and each look takes the checkout's write lock only for its own duration; the wait between them holds no lock,
    /// so a listener or another verb is never kept out. A busy checkout at one look is tried again at the next.
    /// </summary>
    private static async Task<EnvelopeTrace?> WaitForReportAsync(ServiceProvider services, EnvelopeId id, TimeSpan timeout, CancellationToken ct)
    {
        var bus = services.GetRequiredService<BusRepository>();
        var inventory = services.GetRequiredService<BusInventory>();
        TimeProvider clock = services.GetRequiredService<TimeProvider>();
        DateTimeOffset until = clock.GetUtcNow() + timeout;
        while (true)
        {
            await bus.PullAsync(ct);
            try
            {
                if (await inventory.TraceAsync(id, ct) is { } trace && (trace.NewestReport is not null || IsRejectedWithoutReport(trace)))
                {
                    return trace;
                }
            }
            catch (BusBusyException)
            {
                // another zyggy process holds the checkout; the next look tries again
            }

            TimeSpan left = until - clock.GetUtcNow();
            if (left <= TimeSpan.Zero)
            {
                return null;
            }

            await Task.Delay(left < PullEvery ? left : PullEvery, clock, ct);
        }
    }

    /// <summary>
    /// Spec 59 slice 9 correction: the job sits in <c>jobs/rejected/</c> and no report answers it — the node or the sweep moved it there
    /// without one because its sender could not be read — so no report will ever come and the wait need not run to its timeout.
    /// </summary>
    private static bool IsRejectedWithoutReport(EnvelopeTrace trace) =>
        trace.State == TraceState.Rejected && trace.Job is not null && trace.Reports.Count == 0;

    private sealed record FileWanted(string? Out);

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
