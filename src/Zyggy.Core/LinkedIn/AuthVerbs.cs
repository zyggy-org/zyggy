using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

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

/// <summary>
/// <c>zyggy linkedin auth finish</c> (spec 36 AC-17): reads the address the browser landed on from stdin and, only when it answers the
/// pending, fresh, uncancelled sign-in at the registered redirect, exchanges its code once, reads the account once, refuses another
/// account than the pinned one and stores the token. The code, the client secret and the token are never printed.
/// Exit 0 · 3 (configuration, client secret) · 4 (usage) · 5 (refused) · 6 (exchange or account lookup failed).
/// </summary>
internal sealed class AuthFinishVerb(LinkedInVerbContext context) : ILinkedInVerb
{
    public const int MaxLine = 4096;
    public static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(30);

    public async Task<int> RunAsync(IReadOnlyList<string> args, VerbIo io, CancellationToken cancellationToken)
    {
        if (args.Count > 0)
        {
            await io.Error.WriteAsync("linkedin: auth finish takes no arguments (the address comes on stdin)\n").ConfigureAwait(false);
            return 4;
        }

        var line = await io.In.ReadLineAsync(cancellationToken).ConfigureAwait(false) ?? string.Empty;
        if (line.Length > MaxLine)
        {
            await io.Error.WriteAsync($"linkedin: auth finish reads one line of at most {MaxLine} characters\n").ConfigureAwait(false);
            return 4;
        }

        var (session, error) = LinkedInSession.Load(context);
        var endpoints = LinkedInEndpoints.Resolve(context.Environment);
        if (session is null || endpoints.Routes is null)
        {
            await io.Error.WriteAsync($"linkedin: {error ?? endpoints.Error}\n").ConfigureAwait(false);
            return 3;
        }

        var (refusal, code) = Check(session, line.Trim());
        if (refusal is not null)
        {
            await io.Out.WriteAsync($"refused: {refusal}\n").ConfigureAwait(false);
            return 5;
        }

        ReadOnlyMemory<byte>? secret;
        try
        {
            secret = await session.Store.GetAsync(session.Tenant, LinkedInSession.ClientSecretName, cancellationToken).ConfigureAwait(false);
        }
        catch (CredentialRefusedException ex)
        {
            await io.Error.WriteAsync(ex.Message + "\n").ConfigureAwait(false);
            return 3;
        }

        if (secret is not { } clientSecret)
        {
            var where = session.Paths.CredentialsDirectorySecret is { } copy ? $"{copy} or {session.Paths.ClientSecretFile}" : session.Paths.ClientSecretFile;
            await io.Error.WriteAsync($"linkedin: no client secret at {where} — runbook \"Install the LinkedIn client secret\"\n").ConfigureAwait(false);
            return 3;
        }

        using var http = new LinkedInHttp(context.LinkedInHandler, context.HttpTimeout, endpoints.Routes.Loopback);
        var api = new LinkedInApi(http, endpoints.Routes);
        TokenExchangeResult exchange;
        try
        {
            exchange = await api.ExchangeCodeAsync(code!, session.Configuration.ClientId, clientSecret, session.Configuration.RedirectUri, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            LinkedInSession.Clear(clientSecret);
        }

        if (exchange.AccessToken is not { } accessToken)
        {
            await io.Error.WriteAsync($"linkedin: token exchange failed ({exchange.Error})\n").ConfigureAwait(false);
            return 6;
        }

        var user = await api.GetUserInfoAsync(accessToken, cancellationToken).ConfigureAwait(false);
        if (user.Sub is not { } sub || user.Name is not { } name)
        {
            await io.Error.WriteAsync("linkedin: account lookup failed\n").ConfigureAwait(false);
            return 6;
        }

        if (session.Configuration.MemberSub is { } pinned && pinned != sub)
        {
            await io.Out.WriteAsync("refused: a different LinkedIn account\n").ConfigureAwait(false);
            return 5;
        }

        var now = context.Clock.GetUtcNow();
        var token = new LinkedInToken(1, accessToken, now.AddSeconds(exchange.ExpiresInSeconds!.Value), exchange.Scope ?? string.Empty, sub, name, now);
        var bytes = token.ToBytes();
        try
        {
            await session.Store.SetAsync(session.Tenant, LinkedInSession.TokenName, bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (CredentialRefusedException ex)
        {
            await io.Error.WriteAsync(ex.Message + "\n").ConfigureAwait(false);
            return 3;
        }
        catch (StateDirectoryException ex)
        {
            await io.Error.WriteAsync($"linkedin: configuration error: {ex.Message}\n").ConfigureAwait(false);
            return 3;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }

        OAuthPending.Delete(session.Paths);
        var date = session.LocalDate(token.ExpiresAt).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        await io.Out.WriteAsync($"connected: {name}, expires {date}\n").ConfigureAwait(false);
        return 0;
    }

    // Steps 1–6 of the plan, in order: the refusal text and the code. A bad answer (steps 3–6) uses up the pending state.
    private (string? Refusal, string? Code) Check(LinkedInSession session, string line)
    {
        if (OAuthPending.Read(session.Paths) is not { } pending)
        {
            return ("no pending connection — say \"connect LinkedIn\"", null);
        }

        if (!Uri.TryCreate(line, UriKind.Absolute, out var landed)
            || !string.Equals(landed.GetLeftPart(UriPartial.Path), new Uri(session.Configuration.RedirectUri).GetLeftPart(UriPartial.Path), StringComparison.Ordinal))
        {
            return ("not the registered redirect address", null);
        }

        var query = System.Web.HttpUtility.ParseQueryString(landed.Query);
        string? refusal = null;
        if (query["error"] is { } cancelled)
        {
            refusal = $"sign-in cancelled ({LinkedInApi.Code(cancelled) ?? "unknown"})";
        }
        else if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(query["state"] ?? string.Empty), Encoding.UTF8.GetBytes(pending.State)))
        {
            refusal = "state mismatch";
        }
        else if (context.Clock.GetUtcNow() - pending.Created > PendingLifetime)
        {
            refusal = "sign-in link expired";
        }
        else if (query["code"] is not { Length: > 0 })
        {
            refusal = "no code in the address";
        }

        if (refusal is not null)
        {
            OAuthPending.Delete(session.Paths);
            return (refusal, null);
        }

        return (null, query["code"]);
    }
}
