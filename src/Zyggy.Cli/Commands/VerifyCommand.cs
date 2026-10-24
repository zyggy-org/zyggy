using System.CommandLine;
using System.Globalization;
using System.Text;

using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Verbs;

namespace Zyggy.Cli.Commands;

/// <summary>
/// <c>zyggy verify</c> (founding spec §8 Audit as amended by A8-2; spec 08 AC-11): the §8 audit as a command — one <c>FAIL</c> line per
/// drift, one <c>WARN</c> per entry under <c>tenants/</c> that is not this tenant, <c>OK</c>/<c>SKIP</c> lines with <c>--verbose</c>, then the
/// summary. Read-only; exit 0 clean, 6 drift, 3 configuration.
/// </summary>
internal static class VerifyCommand
{
    public static Command Create(CliEnvironment environment, VerbIo io, Action<IServiceCollection>? configure)
    {
        var command = new Command("verify", "Re-check every signature and the §4 write rules of this tenant's subtree; report drift.") { BusHost.ConfigOption, BusHost.VerboseOption };
        command.SetAction((parseResult, ct) => RunAsync(environment, io, configure, parseResult, ct));
        return command;
    }

    private static async Task<int> RunAsync(CliEnvironment environment, VerbIo io, Action<IServiceCollection>? configure, ParseResult parseResult, CancellationToken ct)
    {
        (NodeConfiguration? configuration, int exitCode) = BusHost.LoadConfiguration(parseResult, environment, io);
        if (configuration is null)
        {
            return exitCode;
        }

        bool verbose = parseResult.GetValue(BusHost.VerboseOption);
        await using ServiceProvider services = BusHost.BuildServices(configuration, verbose, environment, configure);
        int checkout = await BusHost.CheckCheckoutAsync(services, configuration, io, ct);
        if (checkout != ExitCodes.Ok)
        {
            return checkout;
        }

        AuditReport report;
        try
        {
            IProgress<int>? progress = verbose ? new Progress(io) : null;
            report = await services.GetRequiredService<BusAuditor>().RunAsync(progress, ct);
        }
        catch (BusBusyException ex)
        {
            return BusHost.ConfigurationError(io, ex.Message);
        }

        await io.Out.WriteAsync(Render(report, verbose));
        return report.IsClean ? ExitCodes.Ok : ExitCodes.Failed;
    }

    /// <summary>The spec's "Output formats" block for <c>zyggy verify</c>.</summary>
    internal static string Render(AuditReport report, bool verbose)
    {
        var sb = new StringBuilder();
        var okPerFolder = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (AuditEntry entry in report.Entries)
        {
            switch (entry.Outcome)
            {
                case AuditOutcome.Ok:
                    if (verbose)
                    {
                        sb.Append("OK ").Append(entry.RelativePath).Append('\n');
                    }

                    string folder = entry.RelativePath[..entry.RelativePath.LastIndexOf('/')];
                    okPerFolder[folder] = okPerFolder.GetValueOrDefault(folder) + 1;
                    break;
                case AuditOutcome.Skipped:
                    if (verbose)
                    {
                        sb.Append("SKIP ").Append(entry.RelativePath).Append(' ').Append(entry.Detail).Append('\n');
                    }

                    break;
                default:
                    sb.Append("FAIL ").Append(entry.RelativePath).Append(' ').Append(AuditWire.ToWire(entry.Outcome));
                    if (entry.Field is { } field)
                    {
                        sb.Append(" (").Append(field).Append(')');
                    }

                    if (entry.Outcome == AuditOutcome.UnknownKey && entry.Detail is { } detail && detail.IndexOf(':', StringComparison.Ordinal) is > 0 and var colon)
                    {
                        sb.Append(" (").Append(detail[..colon]).Append(')');
                    }

                    sb.Append('\n');
                    break;
            }
        }

        foreach (string tenant in report.ForeignTenants)
        {
            sb.Append("WARN ").Append(BusPaths.Tenants).Append('/').Append(tenant).Append("/ second tenant directory (§13: one bus repository per tenant); not read\n");
        }

        foreach (string entry in report.UnexpectedTopLevel)
        {
            sb.Append("WARN ").Append(entry).Append(" unexpected entry under ").Append(BusPaths.Tenants).Append("/; not read\n");
        }

        if (verbose)
        {
            foreach ((string folder, int count) in okPerFolder)
            {
                sb.Append(folder).Append(": ").Append(count.ToString(CultureInfo.InvariantCulture)).Append(" ok\n");
            }
        }

        int tenants = report.ForeignTenants.Count + 1;
        sb.Append("verified ").Append(report.Ok.ToString(CultureInfo.InvariantCulture)).Append(" ok, ")
            .Append(report.Failed.ToString(CultureInfo.InvariantCulture)).Append(" failed, ")
            .Append(report.Skipped.ToString(CultureInfo.InvariantCulture)).Append(" skipped; ")
            .Append(tenants.ToString(CultureInfo.InvariantCulture)).Append(tenants == 1 ? " tenant directory; HEAD " : " tenant directories; HEAD ")
            .Append(report.HeadSha is { Length: > 12 } sha ? sha[..12] : report.HeadSha ?? "unknown").Append('\n');
        return sb.ToString();
    }

    /// <summary>Progress lines on the error stream with <c>--verbose</c>, reported on the calling thread.</summary>
    private sealed class Progress(VerbIo io) : IProgress<int>
    {
        public void Report(int value) => io.Error.WriteLine("verified " + value.ToString(CultureInfo.InvariantCulture) + " files…");
    }
}
