using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;

using Zyggy.Core.M365;
using Zyggy.Core.Secrets;
using Zyggy.Core.Verbs;

namespace Zyggy.Core.LinkedIn;

/// <summary>
/// <c>zyggy linkedin auth start</c> (spec 36 AC-16): prints the one sign-in link with a fresh 32-byte <c>state</c> and remembers that state
/// with its creation time in <c>pending-auth.json</c> (0600). Exit 0 · 3 (configuration) · 4 (any argument).
/// </summary>
internal sealed class AuthStartVerb(LinkedInVerbContext context) : ILinkedInVerb
{
    public async Task<int> RunAsync(IReadOnlyList<string> args, VerbIo io, CancellationToken cancellationToken)
    {
        if (args.Count > 0)
        {
            await io.Error.WriteAsync("linkedin: auth start takes no arguments\n").ConfigureAwait(false);
            return 4;
        }

        var load = LinkedInConfiguration.Load(context.Environment);
        if (load.Configuration is not { } config)
        {
            await io.Error.WriteAsync($"linkedin: {load.Error}\n").ConfigureAwait(false);
            return 3;
        }

        var paths = new LinkedInPaths(context.Environment);
        var state = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        try
        {
            new OAuthPending(state, context.Clock.GetUtcNow()).Write(paths);
        }
        catch (StateDirectoryException ex)
        {
            await io.Error.WriteAsync($"linkedin: configuration error: {ex.Message}\n").ConfigureAwait(false);
            return 3;
        }

        var url = $"{LinkedInEndpoints.AuthorizationUrl}?response_type=code&client_id={Uri.EscapeDataString(config.ClientId)}"
            + $"&redirect_uri={Uri.EscapeDataString(config.RedirectUri)}&state={state}&scope={Uri.EscapeDataString(LinkedInEndpoints.Scopes)}";
        await io.Out.WriteAsync(url + "\n").ConfigureAwait(false);
        return 0;
    }
}

/// <summary>
/// <c>zyggy linkedin auth status</c> (spec 36 AC-18): whether LinkedIn is connected and until when, a warning from
/// <c>expiry_warn_days</c> before the end, and a missing <c>w_member_social</c> scope. Exit 0 · 3 (configuration, credential refused)
/// · 4 (any argument) · 5 (not connected, expired, scope missing). Never prints the token.
/// </summary>
internal sealed class AuthStatusVerb(LinkedInVerbContext context) : ILinkedInVerb
{
    private const string Reconnect = "say \"connect LinkedIn\"";

    public async Task<int> RunAsync(IReadOnlyList<string> args, VerbIo io, CancellationToken cancellationToken)
    {
        if (args.Count > 0)
        {
            await io.Error.WriteAsync("linkedin: auth status takes no arguments\n").ConfigureAwait(false);
            return 4;
        }

        var (session, error) = LinkedInSession.Load(context);
        if (session is null)
        {
            await io.Error.WriteAsync($"linkedin: {error}\n").ConfigureAwait(false);
            return 3;
        }

        LinkedInToken? token;
        try
        {
            token = await session.ReadTokenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (CredentialRefusedException ex)
        {
            await io.Error.WriteAsync(ex.Message + "\n").ConfigureAwait(false);
            return 3;
        }

        if (token is null)
        {
            await io.Out.WriteAsync("not connected — runbook \"Connect LinkedIn\"\n").ConfigureAwait(false);
            return 5;
        }

        var now = context.Clock.GetUtcNow();
        var expires = session.LocalDate(token.ExpiresAt);
        var date = expires.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (token.ExpiresAt <= now)
        {
            await io.Out.WriteAsync($"expired {date} — {Reconnect}\n").ConfigureAwait(false);
            return 5;
        }

        if (!token.HasScope(LinkedInToken.PostScope))
        {
            await io.Out.WriteAsync($"connected without scope {LinkedInToken.PostScope} — {Reconnect}\n").ConfigureAwait(false);
            return 5;
        }

        var days = expires.DayNumber - session.LocalDate(now).DayNumber;
        var warning = days <= session.Configuration.ExpiryWarnDays ? $" — reconnect soon: {Reconnect}" : string.Empty;
        await io.Out.WriteAsync($"connected: {token.Name}, expires {date} ({days.ToString(CultureInfo.InvariantCulture)} days){warning}\n").ConfigureAwait(false);
        return 0;
    }
}
