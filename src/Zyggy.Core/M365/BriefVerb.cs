using Zyggy.Core.M365.Runs;
using Zyggy.Core.Verbs;

namespace Zyggy.Core.M365;

/// <summary>
/// <c>zyggy m365 brief</c>: the morning brief, the unit's entry point — <c>brief.sh</c> (spec 33 AC-30). Exit 0 done or already created
/// · 3 configuration · 4 usage · 5 audit flagged · 6 identity, Graph or model-run failure · 130/143 stopped by a signal. Allowed
/// unattended (the unit sets <c>ZYGGY_HOOKS=off</c>).
/// </summary>
internal sealed class BriefVerb(M365VerbContext context) : IM365Verb
{
    private const string Prefix = "m365-brief: ";

    public async Task<int> RunAsync(IReadOnlyList<string> args, VerbIo io, CancellationToken cancellationToken)
    {
        if (args.Count > 0)
        {
            await io.Error.WriteAsync(Prefix + "brief takes no argument (usage: zyggy m365 brief)\n").ConfigureAwait(false);
            return 4;
        }

        var (setup, exit, error, warning) = RunPreflight.Load(context, principalFromSettings: false);
        if (warning is not null)
        {
            await io.Error.WriteAsync(warning + "\n").ConfigureAwait(false);
        }

        if (setup is null)
        {
            await io.Error.WriteAsync(Prefix + error + "\n").ConfigureAwait(false);
            return exit;
        }

        using var graph = setup.Session.CreateGraph(context);
        var outcome = await new BriefRun(setup.Session, setup.Partition, graph.Reader, setup.Model, context.Clock)
            .RunAsync(cancellationToken, context.SignalExit).ConfigureAwait(false);
        return await RunOutput.WriteAsync(io, outcome).ConfigureAwait(false);
    }
}

/// <summary>Writes a run's lines: stderr first as the shell interleaved them for a failure, stdout after.</summary>
internal static class RunOutput
{
    public static async Task<int> WriteAsync(VerbIo io, RunOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(io);
        ArgumentNullException.ThrowIfNull(outcome);
        foreach (var line in outcome.StderrLines)
        {
            await io.Error.WriteAsync(line + "\n").ConfigureAwait(false);
        }

        foreach (var line in outcome.StdoutLines)
        {
            await io.Out.WriteAsync(line + "\n").ConfigureAwait(false);
        }

        return outcome.Exit;
    }
}
