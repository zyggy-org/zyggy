using System.Globalization;

using Zyggy.Core.M365;
using Zyggy.Core.Processes;
using Zyggy.Core.Verbs;

namespace Zyggy.Core.Brief;

/// <summary>
/// <c>zyggy brief items &lt;Zn[,Zm…]|Zn-Zm|all&gt; [--date &lt;YYYY-MM-DD&gt;]</c> (spec 35 AC-24): one JSON line per selected item, from that
/// date's item list (default today) with its status from Graph; the "file the other mails" item as one line per mail. Reads only: the
/// session then acts on each <c>ok</c> line through the guarded, prompted, logged action tool. A Graph failure prints no line.
/// Exit 0 · 3 configuration or no item list · 4 usage · 6 identity or Graph failure.
/// </summary>
internal sealed class BriefItemsVerb(BriefVerbContext context)
{
    private const string Prefix = "brief: ";
    private const string Usage = " (usage: zyggy brief items <Zn[,Zm…]|Zn-Zm|all> [--date <YYYY-MM-DD>])";

    public async Task<int> RunAsync(IReadOnlyList<string> args, VerbIo io, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(io);
        ZSelection? selection = null;
        DateOnly? date = null;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--date" && date is null && i + 1 < args.Count
                && DateOnly.TryParseExact(args[i + 1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                date = parsed;
                i++;
            }
            else if (selection is null && !args[i].StartsWith('-') && ZSelector.TryParse(args[i], out var selected))
            {
                selection = selected;
            }
            else
            {
                return await FailAsync(io, 4, $"unexpected argument '{ShellText.Prefix(args[i], 40)}'{Usage}").ConfigureAwait(false);
            }
        }

        if (selection is null)
        {
            return await FailAsync(io, 4, "no items selected" + Usage).ConfigureAwait(false);
        }

        var (zone, zoneError) = context.Zone();
        if (zone is null)
        {
            return await FailAsync(io, 3, zoneError!).ConfigureAwait(false);
        }

        var (sidecar, sidecarError) = context.Sidecar(date ?? context.Today(zone));
        if (sidecar is null)
        {
            return await FailAsync(io, 3, sidecarError!).ConfigureAwait(false);
        }

        var m365 = new M365VerbContext(context.Environment, context.Clock, context.FindTimeZone, new ProcessRunner(context.Clock), context.GraphHandler, context.CheckKeyOwnership);
        var (session, exit, error) = M365Session.Load(m365, baseOnly: false);
        if (session is null)
        {
            return await FailAsync(io, exit, error!).ConfigureAwait(false);
        }

        if (session.Warning is not null)
        {
            await io.Error.WriteAsync(session.Warning + "\n").ConfigureAwait(false);
        }

        using var graph = session.CreateGraph(m365);
        var (lines, failure) = await BriefItems.ResolveAsync(sidecar, selection, graph.Reader, cancellationToken).ConfigureAwait(false);
        if (failure is not null)
        {
            return await FailAsync(io, failure.ExitCode, failure.Message).ConfigureAwait(false);
        }

        foreach (var line in lines)
        {
            await io.Out.WriteAsync(line + "\n").ConfigureAwait(false);
        }

        return 0;
    }

    private static async Task<int> FailAsync(VerbIo io, int exit, string message)
    {
        await io.Error.WriteAsync(Prefix + message + "\n").ConfigureAwait(false);
        return exit;
    }
}
