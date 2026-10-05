using System.Globalization;
using System.Text;

using Zyggy.Core.M365.Graph;
using Zyggy.Core.Secrets;
using Zyggy.Core.Verbs;

namespace Zyggy.Core.M365;

/// <summary>
/// <c>zyggy m365 token-test [--key new] [--alg PS256|RS256]</c> (spec 33 AC-18, deviation 1): mints a token as <c>graph.sh token</c> did
/// and prints only its size and expiry — never the token. Exit 0 · 3 configuration or key · 4 usage · 6 identity failure.
/// </summary>
internal sealed class TokenTestVerb(M365VerbContext context) : IM365Verb
{
    private const string Prefix = "m365: ";
    private const string Usage = " (usage: zyggy m365 token-test [--key new] [--alg PS256|RS256])";

    public async Task<int> RunAsync(IReadOnlyList<string> args, VerbIo io, CancellationToken cancellationToken)
    {
        var newKey = false;
        var algorithm = AssertionAlgorithm.Ps256;
        for (var i = 0; i < args.Count; i += 2)
        {
            var value = i + 1 < args.Count ? args[i + 1] : string.Empty;
            switch (args[i])
            {
                case "--key":
                    if (value != "new")
                    {
                        return await UsageAsync(io, "--key takes 'new' (the pair from cert-init --rotate)").ConfigureAwait(false);
                    }

                    newKey = true;
                    break;
                case "--alg":
                    switch (value)
                    {
                        case "PS256":
                            algorithm = AssertionAlgorithm.Ps256;
                            break;
                        case "RS256":
                            algorithm = AssertionAlgorithm.Rs256;
                            break;
                        default:
                            return await UsageAsync(io, "--alg must be PS256 or RS256").ConfigureAwait(false);
                    }

                    break;
                default:
                    return await UsageAsync(io, $"token-test: unexpected argument '{args[i]}'").ConfigureAwait(false);
            }
        }

        var (session, exit, error) = M365Session.Load(context, baseOnly: false);
        if (session is null)
        {
            await io.Error.WriteAsync(Prefix + error + "\n").ConfigureAwait(false);
            return exit;
        }

        if (session.Warning is not null)
        {
            await io.Error.WriteAsync(session.Warning + "\n").ConfigureAwait(false);
        }

        using var graph = session.CreateGraph(context);
        var minted = await graph.Tokens.MintAsync(new TokenRequest(newKey, algorithm), cancellationToken).ConfigureAwait(false);
        if (minted.Source != CredentialSource.None)
        {
            await io.Error.WriteAsync($"key: {minted.Source.ToText()}\n").ConfigureAwait(false);
        }

        if (minted.AccessToken is not { } token)
        {
            await io.Error.WriteAsync(Prefix + minted.Error + "\n").ConfigureAwait(false);
            return minted.ExitCode;
        }

        var expires = minted.ExpiresAt!.Value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        await io.Out.WriteAsync($"token ok: {Encoding.UTF8.GetByteCount(token)} bytes, expires {expires}\n").ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> UsageAsync(VerbIo io, string message)
    {
        await io.Error.WriteAsync(Prefix + message + Usage + "\n").ConfigureAwait(false);
        return 4;
    }
}
