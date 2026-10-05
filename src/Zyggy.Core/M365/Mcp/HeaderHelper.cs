using System.Text.RegularExpressions;

using Zyggy.Core.M365.Graph;
using Zyggy.Core.Processes;

namespace Zyggy.Core.M365.Mcp;

/// <summary>The helper's result: the one stdout line on success, the one stderr line on failure.</summary>
internal sealed record HeaderOutcome(int Exit, string? StdoutLine, string? StderrLine);

/// <summary>
/// The headersHelper of the m365 MCP server — <c>mcp-auth-header.sh</c> (spec 23 D8, spec 33 AC-24): a fresh app-only token per
/// connection, printed as exactly <c>{"Authorization":"Bearer &lt;token&gt;"}</c>. A failure other than configuration or usage (3, 4) is
/// retried once; the whole run stays under 8 s (below Claude Code's 10 s helper timeout). The journal (<c>logger -t zyggy-m365</c>) gets
/// one line per outcome. The token leaves only through the stdout line: never an argument, a file, stderr or the journal.
/// </summary>
internal sealed partial class HeaderHelper(ITokenSource tokens, IProcessRunner runner, TimeProvider clock, string? loggerPath)
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(8);
    private const string FailureLine = "m365: token refresh failed — runbook 13 \"Certificate rejected\"";

    public async Task<HeaderOutcome> RunAsync(CancellationToken cancellationToken)
    {
        var deadline = clock.GetUtcNow() + Budget;
        TokenResult? result = null;
        var timedOut = false;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var left = deadline - clock.GetUtcNow();
            if (left <= TimeSpan.Zero)
            {
                timedOut = true;
                break;
            }

            try
            {
                result = await tokens.MintAsync(new TokenRequest(UseNewKey: false, AssertionAlgorithm.Ps256), cancellationToken)
                    .WaitAsync(left, clock, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                timedOut = true;
                result = null;
                break;
            }

            // Configuration and usage errors do not get better on a second try.
            if (result.AccessToken is not null || result.ExitCode is 3 or 4)
            {
                break;
            }
        }

        if (timedOut || result is null)
        {
            return await FailAsync(6, $"token mint did not finish within {Budget.TotalSeconds:0}s", cancellationToken).ConfigureAwait(false);
        }

        if (result.AccessToken is not { } token)
        {
            return await FailAsync(result.ExitCode, result.Error ?? $"token mint exited {result.ExitCode}", cancellationToken).ConfigureAwait(false);
        }

        // A JWT is base64url segments and dots: nothing to escape in the JSON, and anything else is not a token.
        if (!Jwt().IsMatch(token))
        {
            return await FailAsync(6, "the token mint printed no token", cancellationToken).ConfigureAwait(false);
        }

        await JournalAsync("token minted", cancellationToken).ConfigureAwait(false);
        return new HeaderOutcome(0, $$"""{"Authorization":"Bearer {{token}}"}""", null);
    }

    private async Task<HeaderOutcome> FailAsync(int exit, string reason, CancellationToken cancellationToken)
    {
        await JournalAsync("token refresh failed: " + reason, cancellationToken).ConfigureAwait(false);
        return new HeaderOutcome(exit, null, FailureLine);
    }

    // logger -t zyggy-m365 -- <message>: never fatal, never the token.
    private async Task JournalAsync(string message, CancellationToken cancellationToken)
    {
        if (loggerPath is null)
        {
            return;
        }

        try
        {
            await runner.RunAsync(
                new ProcessSpec(loggerPath, ["-t", "zyggy-m365", "--", message], Environment.CurrentDirectory) { Timeout = TimeSpan.FromSeconds(2) },
                cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The journal is best effort, as `logger … || true`.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
        }
    }

    [GeneratedRegex(@"\A[A-Za-z0-9_-]+(\.[A-Za-z0-9_-]+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex Jwt();
}
