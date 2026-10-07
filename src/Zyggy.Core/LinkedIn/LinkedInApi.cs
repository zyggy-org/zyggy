using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Zyggy.Core.LinkedIn;

/// <summary>
/// The LinkedIn adapter over <see cref="LinkedInHttp"/>: the token exchange, the user info and (Step 5) the post. One attempt per call;
/// no token, secret or code ever reaches an exception message or a result's error.
/// </summary>
internal sealed partial class LinkedInApi(LinkedInHttp http, LinkedInRoutes routes) : ILinkedInApi
{
    public async Task<TokenExchangeResult> ExchangeCodeAsync(
        string code, string clientId, ReadOnlyMemory<byte> clientSecret, string redirectUri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(clientId);
        ArgumentNullException.ThrowIfNull(redirectUri);
        using var request = new HttpRequestMessage(HttpMethod.Post, routes.AccessTokenUrl)
        {
            Content = new FormUrlEncodedContent(
            [
                new("grant_type", "authorization_code"),
                new("code", code),
                new("client_id", clientId),
                new("client_secret", Encoding.UTF8.GetString(clientSecret.Span)),
                new("redirect_uri", redirectUri),
            ]),
        };

        var (status, body) = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (status is null)
        {
            return new TokenExchangeResult(null, null, null, "request_failed");
        }

        using var document = Parse(body);
        var root = document?.RootElement;
        if (status != 200)
        {
            return new TokenExchangeResult(null, null, null, Code(String(root, "error")) ?? $"http_{status}");
        }

        var token = String(root, "access_token");
        var expiresIn = root is { ValueKind: JsonValueKind.Object } r && r.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var seconds) && seconds > 0
            ? seconds
            : (int?)null;
        return token is { Length: > 0 } && expiresIn is not null
            ? new TokenExchangeResult(token, expiresIn, String(root, "scope") ?? string.Empty, null)
            : new TokenExchangeResult(null, null, null, "invalid_response");
    }

    public async Task<UserInfoResult> GetUserInfoAsync(string accessToken, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(accessToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, routes.UserInfoUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var (status, body) = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (status is null)
        {
            return new UserInfoResult(null, null, "request_failed");
        }

        if (status != 200)
        {
            return new UserInfoResult(null, null, $"http_{status}");
        }

        using var document = Parse(body);
        var sub = String(document?.RootElement, "sub");
        var name = String(document?.RootElement, "name");
        return sub is { Length: > 0 } && name is { Length: > 0 } ? new UserInfoResult(sub, name, null) : new UserInfoResult(null, null, "invalid_response");
    }

    /// <summary>LinkedIn's error code as a result carries it: only <c>[A-Za-z0-9_.-]</c>, at most 40 characters.</summary>
    public static string? Code(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var cleaned = CodeCharacters().Replace(text, string.Empty);
        cleaned = cleaned.Length > 40 ? cleaned[..40] : cleaned;
        return cleaned.Length == 0 ? null : cleaned;
    }

    // One attempt; a transport failure or a timeout is no status (the caller's own cancellation propagates).
    private async Task<(int? Status, string Body)> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return (null, string.Empty);
        }
    }

    private static JsonDocument? Parse(string body)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? String(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    [GeneratedRegex("[^A-Za-z0-9_.-]", RegexOptions.CultureInvariant)]
    private static partial Regex CodeCharacters();
}
