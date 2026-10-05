using Zyggy.Core.M365.Runs;
using Zyggy.Core.Verbs;

namespace Zyggy.Core.M365;

/// <summary>
/// <c>zyggy m365 mail-backfill [--folder &lt;name&gt;] [--reset]</c> — <c>mail-backfill.sh</c> (spec 33 AC-31). Owner-started only: refused
/// under <c>ZYGGY_HOOKS=off</c> before anything. Exit 0 done · 3 configuration · 4 usage · 5 refused, a cap or a stuck folder · 6 failure ·
/// 130/143 stopped by a signal.
/// </summary>
internal sealed class MailBackfillVerb(M365VerbContext context) : IM365Verb
{
    public Task<int> RunAsync(IReadOnlyList<string> args, VerbIo io, CancellationToken cancellationToken) =>
        BackfillVerb.RunAsync(context, io, args, "mail-backfill", "--folder", async (setup, graph, item, reset) =>
            await new MailBackfill(setup.Session, setup.Partition, graph.Reader, setup.Model, context.Clock)
                .RunAsync(item, reset, cancellationToken, context.SignalExit?.Invoke() ?? 130).ConfigureAwait(false));
}

/// <summary>
/// <c>zyggy m365 files-backfill [--drive &lt;name&gt;] [--reset]</c> — <c>files-backfill.sh</c> (spec 33 AC-32). Owner-started only: refused
/// under <c>ZYGGY_HOOKS=off</c> before anything. Exit 0 done · 3 configuration · 4 usage · 5 refused, a cap or an unconfirmed batch · 6
/// failure · 130/143 stopped by a signal.
/// </summary>
internal sealed class FilesBackfillVerb(M365VerbContext context) : IM365Verb
{
    public Task<int> RunAsync(IReadOnlyList<string> args, VerbIo io, CancellationToken cancellationToken) =>
        BackfillVerb.RunAsync(context, io, args, "files-backfill", "--drive", async (setup, graph, item, reset) =>
            await new FilesBackfill(setup.Session, setup.Partition, graph.Reader, setup.Model, context.Clock)
                .RunAsync(item, reset, cancellationToken, context.SignalExit?.Invoke() ?? 130).ConfigureAwait(false));
}

/// <summary>The shared shape of the two backfill verbs: the unattended refusal, the arguments, the pre-flight, the run.</summary>
internal static class BackfillVerb
{
    public static async Task<int> RunAsync(
        M365VerbContext context,
        VerbIo io,
        IReadOnlyList<string> args,
        string verb,
        string itemOption,
        Func<RunSetup, GraphParts, string?, bool, Task<RunOutcome>> run)
    {
        var prefix = verb + ": ";
        var usage = $" (usage: zyggy m365 {verb} [{itemOption} <name>] [--reset])";
        if (context.Environment.TryGetValue("ZYGGY_HOOKS", out var hooks) && hooks == "off")
        {
            await io.Error.WriteAsync(prefix + "refused: unattended run (ZYGGY_HOOKS=off)\n").ConfigureAwait(false);
            return 5;
        }

        string? item = null;
        var reset = false;
        for (var i = 0; i < args.Count; i++)
        {
            string? error = null;
            if (args[i] == itemOption)
            {
                if (item is not null)
                {
                    error = $"{itemOption} given twice";
                }
                else if (i + 1 >= args.Count || args[i + 1].Length == 0 || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    error = $"{itemOption} needs <name>";
                }
                else
                {
                    item = args[++i];
                }
            }
            else if (args[i] == "--reset")
            {
                error = reset ? "--reset given twice" : null;
                reset = true;
            }
            else
            {
                error = $"unknown argument '{ShellText.Prefix(args[i], 40)}'";
            }

            if (error is not null)
            {
                await io.Error.WriteAsync(prefix + error + usage + "\n").ConfigureAwait(false);
                return 4;
            }
        }

        var (setup, exit, preflightError, warning) = RunPreflight.Load(context, principalFromSettings: true);
        if (warning is not null)
        {
            await io.Error.WriteAsync(warning + "\n").ConfigureAwait(false);
        }

        if (setup is null)
        {
            await io.Error.WriteAsync(prefix + preflightError + "\n").ConfigureAwait(false);
            return exit;
        }

        using var graph = setup.Session.CreateGraph(context);
        var outcome = await run(setup, graph, item, reset).ConfigureAwait(false);
        return await RunOutput.WriteAsync(io, outcome).ConfigureAwait(false);
    }
}
