using System.Globalization;

using Zyggy.Core.M365.Audit;
using Zyggy.Core.Verbs;

namespace Zyggy.Core.M365;

/// <summary>
/// <c>zyggy m365 verify &lt;date YYYY-MM-DD&gt; &lt;window-start YYYY-MM-DDTHH:MM:SSZ&gt;</c>: the post-run audit of the brief's Drafts —
/// <c>verify.sh</c> (spec 33 AC-23). Exit 0 audit ok · 3 configuration · 4 usage · 5 audit flagged · 6 Graph or identity failure (no
/// receipt). Reads only. Accepts <c>ZYGGY_HOOKS=off</c> (the brief run).
/// </summary>
internal sealed class VerifyVerb(M365VerbContext context) : IM365Verb
{
    private const string Prefix = "m365-verify: ";
    private const string Usage = " (usage: zyggy m365 verify <date YYYY-MM-DD> <window-start YYYY-MM-DDTHH:MM:SSZ>)";

    public async Task<int> RunAsync(IReadOnlyList<string> args, VerbIo io, CancellationToken cancellationToken)
    {
        if (args.Count != 2)
        {
            return await FailAsync(io, 4, "needs exactly <date> <window-start>" + Usage).ConfigureAwait(false);
        }

        if (!M365Grammar.Date().IsMatch(args[0])
            || !DateOnly.TryParseExact(args[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return await FailAsync(io, 4, $"'{ShellText.Prefix(args[0], 40)}' is not a date" + Usage).ConfigureAwait(false);
        }

        if (!M365Grammar.Iso().IsMatch(args[1]))
        {
            return await FailAsync(io, 4, $"'{ShellText.Prefix(args[1], 40)}' is not an ISO timestamp" + Usage).ConfigureAwait(false);
        }

        var (session, exit, error) = M365Session.Load(context, baseOnly: false);
        if (session is null)
        {
            return await FailAsync(io, exit, error!).ConfigureAwait(false);
        }

        if (session.Warning is not null)
        {
            await io.Error.WriteAsync(session.Warning + "\n").ConfigureAwait(false);
        }

        using var graph = session.CreateGraph(context);
        var paths = session.Instance.Paths;
        var audit = new DraftAudit(graph.Reader, new M365State(paths, context.Clock), paths, session.Configuration, session.Patterns);
        var outcome = await audit.AuditAsync(date, args[1], cancellationToken).ConfigureAwait(false);
        if (outcome.Error is not null)
        {
            return await FailAsync(io, outcome.Exit, outcome.Error).ConfigureAwait(false);
        }

        await io.Out.WriteAsync(outcome.StdoutLine + "\n").ConfigureAwait(false);
        return outcome.Exit;
    }

    private static async Task<int> FailAsync(VerbIo io, int exit, string message)
    {
        await io.Error.WriteAsync(Prefix + message + "\n").ConfigureAwait(false);
        return exit;
    }
}
