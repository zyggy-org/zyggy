using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

using Zyggy.Core.Memory;

namespace Zyggy.Core.LinkedIn;

/// <summary>
/// The LinkedIn adapter over <see cref="LinkedInHttp"/>: the token exchange, the user info and the post. One attempt per call; no token,
/// secret or code ever reaches an exception message or a result's error; LinkedIn's rejection message is cut, cleaned and secret-guarded.
/// </summary>
internal sealed partial class LinkedInApi(LinkedInHttp http, LinkedInRoutes routes, SecretPatterns? patterns = null) : ILinkedInApi
{
    public const int MessageMax = 200;

    private static readonly JsonWriterOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

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

    public async Task<CreatePostResult> CreatePostAsync(
        string accessToken, string authorUrn, string commentary, PostVisibility visibility, string apiVersion, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(accessToken);
        ArgumentNullException.ThrowIfNull(authorUrn);
        ArgumentNullException.ThrowIfNull(commentary);
        ArgumentNullException.ThrowIfNull(apiVersion);
        using var request = new HttpRequestMessage(HttpMethod.Post, routes.PostsUrl) { Content = new ByteArrayContent(PostBody(authorUrn, commentary, visibility)) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("Linkedin-Version", apiVersion);
        request.Headers.Add("X-Restli-Protocol-Version", "2.0.0");

        int status;
        string body;
        string? id;
        try
        {
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            status = (int)response.StatusCode;
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            id = response.Headers.TryGetValues("x-restli-id", out var values) ? values.FirstOrDefault() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new CreatePostResult(null, LinkedInFailure.OutcomeUnknown, ex is TaskCanceledException ? "timeout" : "connection failed");
        }

        return status switch
        {
            201 when id is not null && PostUrn().IsMatch(id) => new CreatePostResult(id, null, null),
            201 => new CreatePostResult(null, LinkedInFailure.OutcomeUnknown, "201 without a post id"),
            401 => new CreatePostResult(null, LinkedInFailure.TokenExpired, null),
            403 => new CreatePostResult(null, LinkedInFailure.Forbidden, null),
            426 => new CreatePostResult(null, LinkedInFailure.VersionRetired, null),
            429 => new CreatePostResult(null, LinkedInFailure.RateLimited, null),
            400 or 422 => new CreatePostResult(null, LinkedInFailure.Rejected, Message(body) ?? $"LinkedIn answered {status}"),
            >= 400 and < 500 => new CreatePostResult(null, LinkedInFailure.Rejected, $"LinkedIn answered {status}"),
            _ => new CreatePostResult(null, LinkedInFailure.OutcomeUnknown, $"LinkedIn answered {status}"),
        };
    }

    /// <summary>The request body, keys in the contract's order (spec 36 AC-2).</summary>
    public static byte[] PostBody(string authorUrn, string commentary, PostVisibility visibility)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, Compact))
        {
            writer.WriteStartObject();
            writer.WriteString("author", authorUrn);
            writer.WriteString("commentary", commentary);
            writer.WriteString("visibility", visibility.Wire());
            writer.WriteStartObject("distribution");
            writer.WriteString("feedDistribution", "MAIN_FEED");
            writer.WriteStartArray("targetEntities");
            writer.WriteEndArray();
            writer.WriteStartArray("thirdPartyDistributionChannels");
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteString("lifecycleState", "PUBLISHED");
            writer.WriteBoolean("isReshareDisabledByAuthor", false);
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    // LinkedIn's "message": control characters removed, at most 200 characters, withheld when it matches a secret pattern.
    private string? Message(string body)
    {
        using var document = Parse(body);
        if (String(document?.RootElement, "message") is not { Length: > 0 } message)
        {
            return null;
        }

        var cleaned = new string(message.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if ((patterns ?? SecretPatterns.None).TryMatch(cleaned, out _))
        {
            return "[withheld]";
        }

        var runes = cleaned.EnumerateRunes().ToList();
        return runes.Count <= MessageMax ? cleaned : string.Concat(runes.Take(MessageMax).Select(r => r.ToString()));
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

    [GeneratedRegex(@"\Aurn:li:(share|ugcPost):[0-9]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex PostUrn();
}
