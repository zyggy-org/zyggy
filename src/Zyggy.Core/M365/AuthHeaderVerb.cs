using Zyggy.Core.M365.Graph;
using Zyggy.Core.M365.Mcp;
using Zyggy.Core.Verbs;

namespace Zyggy.Core.M365;

/// <summary>
/// <c>zyggy m365 auth-header</c>: the headersHelper named in <c>.mcp.json</c> — <c>mcp-auth-header.sh</c> (spec 23 D8, spec 33 AC-24). Prints
/// exactly one line <c>{"Authorization":"Bearer &lt;token&gt;"}</c>, or nothing on stdout and one fixed stderr line. A configuration error
/// is a failure that is not retried. Accepts <c>ZYGGY_HOOKS=off</c>.
/// </summary>
internal sealed class AuthHeaderVerb(M365VerbContext context) : IM365Verb
{
    public async Task<int> RunAsync(IReadOnlyList<string> args, VerbIo io, CancellationToken cancellationToken)
    {
        if (args.Count > 0)
        {
            await io.Error.WriteAsync("m365: takes no argument (usage: zyggy m365 auth-header)\n").ConfigureAwait(false);
            return 4;
        }

        var logger = ProgramLocator.OnPath("logger", context.Environment);
        var (session, exit, error) = M365Session.Load(context, baseOnly: false);
        HeaderOutcome outcome;
        if (session is null)
        {
            outcome = await new HeaderHelper(new FailedTokens(exit, error!), context.Runner, context.Clock, logger).RunAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            using var graph = session.CreateGraph(context);
            outcome = await new HeaderHelper(graph.Tokens, context.Runner, context.Clock, logger).RunAsync(cancellationToken).ConfigureAwait(false);
        }

        if (outcome.StdoutLine is not null)
        {
            await io.Out.WriteAsync(outcome.StdoutLine + "\n").ConfigureAwait(false);
        }
        else
        {
            await io.Error.WriteAsync(outcome.StderrLine + "\n").ConfigureAwait(false);
        }

        return outcome.Exit;
    }

    // A configuration that does not load is the token mint's own exit 3: journalled, not retried.
    private sealed class FailedTokens(int exit, string error) : ITokenSource
    {
        public Task<TokenResult> MintAsync(TokenRequest request, CancellationToken cancellationToken) => Task.FromResult(TokenResult.Failed(exit, error));
    }
}
