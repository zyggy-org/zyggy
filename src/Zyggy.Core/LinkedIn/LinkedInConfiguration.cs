using System.Text.Json;
using System.Text.RegularExpressions;

namespace Zyggy.Core.LinkedIn;

/// <summary>The outcome of <see cref="LinkedInConfiguration.Load"/>: the configuration, or the first error (exit 3).</summary>
internal sealed record LinkedInConfigurationLoad(LinkedInConfiguration? Configuration, string? Error);

/// <summary>
/// <c>instance/linkedin.json</c> (spec 36 Configuration), the instance being <c>ZYGGY_INSTANCE_DIR</c>, else
/// <c>$CLAUDE_PROJECT_DIR/instance</c>. Absent keys take the code defaults; an unknown key is an error; the instance may only set values,
/// so <c>actions.enabled</c> can only hold <c>post</c> and <c>post.max_chars</c> can only be lowered.
/// </summary>
internal sealed partial record LinkedInConfiguration(
    string Path,
    string ClientId,
    string RedirectUri,
    string? MemberSub,
    string ApiVersion,
    IReadOnlyList<string> ActionsEnabled,
    int PostMaxChars,
    int ExpiryWarnDays)
{
    public const string DefaultApiVersion = "202609";
    public const int MaxPostChars = 3000;
    public const string PostAction = "post";

    private static readonly HashSet<string> TopKeys = new(["client_id", "redirect_uri", "member_sub", "api_version", "actions", "post", "expiry_warn_days"], StringComparer.Ordinal);

    /// <summary>Gets a value indicating whether <c>actions.enabled</c> lists <c>post</c>.</summary>
    public bool PostEnabled => ActionsEnabled.Contains(PostAction, StringComparer.Ordinal);

    public static LinkedInConfigurationLoad Load(IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        string? Get(string key) => environment.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value) ? value : null;

        var instance = Get("ZYGGY_INSTANCE_DIR") ?? (Get("CLAUDE_PROJECT_DIR") is { } project ? System.IO.Path.Join(project, "instance") : null);
        if (instance is null)
        {
            return Fail("configuration error: ZYGGY_INSTANCE_DIR is not set (and no CLAUDE_PROJECT_DIR to derive it from)");
        }

        var path = System.IO.Path.Join(instance, "linkedin.json");
        if (!File.Exists(path))
        {
            return Fail($"configuration error: {path} is missing");
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            return Validate(path, document.RootElement);
        }
        catch (JsonException)
        {
            return Fail($"configuration error: {path}: not valid JSON");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail($"configuration error: {path} cannot be read");
        }
    }

    private static LinkedInConfigurationLoad Validate(string path, JsonElement root)
    {
        LinkedInConfigurationLoad Error(string key, string reason) => Fail($"configuration error: {path}: {key} {reason}");

        if (root.ValueKind != JsonValueKind.Object)
        {
            return Fail($"configuration error: {path}: not a JSON object");
        }

        foreach (var property in root.EnumerateObject())
        {
            if (!TopKeys.Contains(property.Name))
            {
                return Error(property.Name, "is not a known key");
            }
        }

        if (!root.TryGetProperty("client_id", out var clientId))
        {
            return Error("client_id", "is required");
        }

        if (clientId.ValueKind != JsonValueKind.String || !ClientIdShape().IsMatch(clientId.GetString()!))
        {
            return Error("client_id", "must be 1 to 64 letters or digits");
        }

        if (!root.TryGetProperty("redirect_uri", out var redirect))
        {
            return Error("redirect_uri", "is required");
        }

        if (redirect.ValueKind != JsonValueKind.String || !IsRedirectUri(redirect.GetString()!))
        {
            return Error("redirect_uri", "must be an absolute https address (or http on localhost) without query or fragment");
        }

        string? memberSub = null;
        if (root.TryGetProperty("member_sub", out var sub))
        {
            if (sub.ValueKind != JsonValueKind.String || !SubShape().IsMatch(sub.GetString()!))
            {
                return Error("member_sub", "must be 1 to 64 letters, digits, '_' or '-'");
            }

            memberSub = sub.GetString();
        }

        var apiVersion = DefaultApiVersion;
        if (root.TryGetProperty("api_version", out var version))
        {
            if (version.ValueKind != JsonValueKind.String || !VersionShape().IsMatch(version.GetString()!))
            {
                return Error("api_version", "must be YYYYMM");
            }

            apiVersion = version.GetString()!;
        }

        IReadOnlyList<string> enabled = [PostAction];
        if (root.TryGetProperty("actions", out var actions))
        {
            if (Unknown(actions, "enabled") is { } unknownAction)
            {
                return unknownAction.Length == 0 ? Error("actions", "must be an object") : Error("actions." + unknownAction, "is not a known key");
            }

            if (actions.TryGetProperty("enabled", out var list))
            {
                if (list.ValueKind != JsonValueKind.Array
                    || list.GetArrayLength() > 1
                    || list.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String || e.GetString() != PostAction))
                {
                    return Error("actions.enabled", "may only list \"post\", once");
                }

                enabled = [.. list.EnumerateArray().Select(e => e.GetString()!)];
            }
        }

        var maxChars = MaxPostChars;
        if (root.TryGetProperty("post", out var post))
        {
            if (Unknown(post, "max_chars") is { } unknownPost)
            {
                return unknownPost.Length == 0 ? Error("post", "must be an object") : Error("post." + unknownPost, "is not a known key");
            }

            if (post.TryGetProperty("max_chars", out var max))
            {
                if (Integer(max, 1, MaxPostChars) is not { } value)
                {
                    return Error("post.max_chars", $"must be an integer 1..{MaxPostChars}");
                }

                maxChars = value;
            }
        }

        var warnDays = 7;
        if (root.TryGetProperty("expiry_warn_days", out var warn))
        {
            if (Integer(warn, 1, 30) is not { } value)
            {
                return Error("expiry_warn_days", "must be an integer 1..30");
            }

            warnDays = value;
        }

        return new LinkedInConfigurationLoad(
            new LinkedInConfiguration(path, clientId.GetString()!, redirect.GetString()!, memberSub, apiVersion, enabled, maxChars, warnDays),
            null);
    }

    // "" when the section is not an object; the first key other than the allowed one; null when the section is fine.
    private static string? Unknown(JsonElement section, string allowed)
    {
        if (section.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        return section.EnumerateObject().Select(p => p.Name).FirstOrDefault(name => name != allowed);
    }

    // An absolute https address, or http whose host is localhost; no query, no fragment, no user information.
    private static bool IsRedirectUri(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.Host == "localhost"))
        && !text.Contains('?', StringComparison.Ordinal)
        && !text.Contains('#', StringComparison.Ordinal)
        && uri.UserInfo.Length == 0;

    private static int? Integer(JsonElement element, int min, int max) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value) && value >= min && value <= max ? value : null;

    private static LinkedInConfigurationLoad Fail(string message) => new(null, message);

    [GeneratedRegex(@"\A[A-Za-z0-9]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ClientIdShape();

    [GeneratedRegex(@"\A[A-Za-z0-9_-]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SubShape();

    [GeneratedRegex(@"\A[0-9]{6}\z", RegexOptions.CultureInvariant)]
    private static partial Regex VersionShape();
}
