using System.Text.Json;
using System.Text.RegularExpressions;

using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Configuration;

/// <summary>
/// Reads <c>node.json</c> (founding spec §10, as amended by deliverable 04): the minimal keys typed and validated, unknown keys
/// ignored, and any key that names a secret (<c>pat</c>, <c>token</c>, <c>hmac</c>, <c>secret</c>, <c>password</c>; any depth,
/// any case) refused — secrets never live in <c>node.json</c>. A missing <c>tenant</c> is a refusal: there is no default tenant.
/// </summary>
public static partial class NodeConfigurationReader
{
    /// <summary>The environment variable that names the file when <c>--config</c> is not given.</summary>
    public const string PathVariable = "ZYGGY_NODE_JSON";

    private const string FileName = "node.json";

    private static readonly string[] SecretKeyWords = ["pat", "token", "hmac", "secret", "password"];

    private static readonly JsonDocumentOptions DocumentOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>
    /// Resolves where <c>node.json</c> is read from: <c>--config</c>, then <see cref="PathVariable"/>, then
    /// <c>%LOCALAPPDATA%\zyggy\node.json</c> on Windows or <c>$XDG_CONFIG_HOME/zyggy/node.json</c> (<c>~/.config/zyggy/node.json</c>) elsewhere.
    /// </summary>
    /// <param name="configOption">The <c>--config</c> value, when given.</param>
    /// <param name="environment">The process environment; an empty value counts as unset.</param>
    /// <returns>The path.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="environment"/> is null.</exception>
    public static string ResolvePath(string? configOption, IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (!string.IsNullOrEmpty(configOption))
        {
            return configOption;
        }

        if (environment.TryGetValue(PathVariable, out string? fromEnvironment) && !string.IsNullOrEmpty(fromEnvironment))
        {
            return fromEnvironment;
        }

        if (OperatingSystem.IsWindows())
        {
            string local = environment.TryGetValue("LOCALAPPDATA", out string? appData) && !string.IsNullOrEmpty(appData)
                ? appData
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Join(local, "zyggy", FileName);
        }

        string config = environment.TryGetValue("XDG_CONFIG_HOME", out string? xdg) && !string.IsNullOrEmpty(xdg)
            ? xdg
            : Path.Join(environment.TryGetValue("HOME", out string? home) && !string.IsNullOrEmpty(home) ? home : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Join(config, "zyggy", FileName);
    }

    /// <summary>Reads and validates the file at <paramref name="path"/>.</summary>
    /// <param name="path">The file path.</param>
    /// <returns>The typed configuration or the first refusal.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="path"/> is null or empty.</exception>
    public static NodeConfigurationResult Read(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path))
        {
            return NodeConfigurationResult.Fail(NodeConfigurationError.FileMissing, null, "node.json not found at " + path);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllBytes(path), DocumentOptions);
        }
        catch (JsonException ex)
        {
            return NodeConfigurationResult.Fail(NodeConfigurationError.Malformed, null, "node.json is not valid JSON: " + ex.Message);
        }

        using (document)
        {
            return Interpret(document.RootElement);
        }
    }

    private static NodeConfigurationResult Interpret(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return NodeConfigurationResult.Fail(NodeConfigurationError.Malformed, null, "node.json must be a JSON object");
        }

        if (FindSecretKey(root, string.Empty) is { } secretKey)
        {
            return NodeConfigurationResult.Fail(NodeConfigurationError.InvalidField, secretKey, "secrets never in node.json");
        }

        if (!TryRequiredString(root, "tenant", out string? tenantText, out NodeConfigurationResult? failure))
        {
            return failure;
        }

        if (!TenantId.TryParse(tenantText, out TenantId? tenant))
        {
            return Invalid("tenant", "tenant invalid: must be a lowercase label");
        }

        if (!TryRequiredString(root, "machine", out string? machineText, out failure))
        {
            return failure;
        }

        if (!MachineName.TryParse(machineText, out MachineName? machine))
        {
            return Invalid("machine", "must be a lowercase label");
        }

        KeyId? signWith = null;
        if (TryOptionalString(root, "signWith", out string? signWithText, out failure) is false)
        {
            return failure!;
        }

        if (signWithText is not null)
        {
            if (!KeyId.TryParse(signWithText, out signWith))
            {
                return Invalid("signWith", "must be <tenant>/<n>");
            }

            if (signWith.Tenant != tenant)
            {
                return Invalid("signWith", "tenant differs");
            }
        }

        if (!root.TryGetProperty("bus", out JsonElement bus) || bus.ValueKind != JsonValueKind.Object)
        {
            return NodeConfigurationResult.Fail(NodeConfigurationError.MissingField, "bus", "bus section missing");
        }

        if (!TryRequiredString(bus, "owner", out string? owner, out failure, "bus.")
            || !TryRequiredString(bus, "repo", out string? repo, out failure, "bus.")
            || !TryRequiredString(bus, "localPath", out string? localPath, out failure, "bus.")
            || !TryRequiredString(bus, "remote", out string? remote, out failure, "bus."))
        {
            return failure;
        }

        if (TryOptionalString(bus, "branch", out string? branch, out failure, "bus.") is false)
        {
            return failure!;
        }

        if (!Path.IsPathRooted(localPath))
        {
            return Invalid("bus.localPath", "must be an absolute path");
        }

        var poll = new PollSettings();
        if (root.TryGetProperty("poll", out JsonElement pollElement))
        {
            if (pollElement.ValueKind != JsonValueKind.Object)
            {
                return Invalid("poll", "must be an object");
            }

            if (!TryPositiveInt(pollElement, "activeSeconds", poll.ActiveSeconds, out int active, out failure)
                || !TryPositiveInt(pollElement, "idleSeconds", poll.IdleSeconds, out int idle, out failure))
            {
                return failure;
            }

            poll = new PollSettings(active, idle);
        }

        var secrets = new SecretsSettings();
        if (root.TryGetProperty("secrets", out JsonElement secretsElement))
        {
            if (secretsElement.ValueKind != JsonValueKind.Object)
            {
                return Invalid("secrets", "must be an object");
            }

            if (TryOptionalString(secretsElement, "root", out string? secretsRoot, out failure, "secrets.") is false)
            {
                return failure!;
            }

            secrets = new SecretsSettings(secretsRoot);
        }

        return NodeConfigurationResult.Ok(new NodeConfiguration(
            tenant,
            machine,
            signWith,
            new BusSettings(owner!, repo!, string.IsNullOrEmpty(branch) ? "main" : branch, localPath!, remote!),
            poll,
            secrets));
    }

    /// <summary>Depth-first: the first key, at any depth, that contains a secret word (case-insensitive), as a dotted path.</summary>
    private static string? FindSecretKey(JsonElement element, string prefix)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            string path = prefix + property.Name;
            if (NamesASecret(property.Name) && !IsAllowedSecretsSection(path))
            {
                return path;
            }

            if (property.Value.ValueKind == JsonValueKind.Object && FindSecretKey(property.Value, path + ".") is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    /// <summary>Whether a key names a secret: one of its words (camelCase, kebab or snake segments) is a secret word or its plural — <c>localPath</c> is not, <c>GitHubToken</c> and <c>Secrets</c> are.</summary>
    private static bool NamesASecret(string key) =>
        KeyWords().Matches(key).Select(m => m.Value.ToLowerInvariant())
            .Any(token => SecretKeyWords.Any(word => token == word || token == word + "s"));

    [GeneratedRegex(@"[A-Z]+(?![a-z])|[A-Z]?[a-z0-9]+")]
    private static partial Regex KeyWords();

    // The documented `secrets` section names the store's location, not a secret; only that exact top-level key is allowed.
    private static bool IsAllowedSecretsSection(string path) => string.Equals(path, "secrets", StringComparison.Ordinal);

    private static bool TryRequiredString(JsonElement element, string key, out string? value, out NodeConfigurationResult failure, string prefix = "")
    {
        value = null;
        failure = null!;
        if (!element.TryGetProperty(key, out JsonElement property) || property.ValueKind == JsonValueKind.Null)
        {
            failure = NodeConfigurationResult.Fail(NodeConfigurationError.MissingField, prefix + key, prefix + key + " missing");
            return false;
        }

        if (property.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(property.GetString()))
        {
            failure = Invalid(prefix + key, "must be a non-empty string");
            return false;
        }

        value = property.GetString();
        return true;
    }

    /// <summary>True with a value, true with null when absent, false (with the failure) when present but not a string.</summary>
    private static bool? TryOptionalString(JsonElement element, string key, out string? value, out NodeConfigurationResult? failure, string prefix = "")
    {
        value = null;
        failure = null;
        if (!element.TryGetProperty(key, out JsonElement property) || property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            failure = Invalid(prefix + key, "must be a string");
            return false;
        }

        value = property.GetString();
        return true;
    }

    private static bool TryPositiveInt(JsonElement element, string key, int fallback, out int value, out NodeConfigurationResult failure)
    {
        value = fallback;
        failure = null!;
        if (!element.TryGetProperty(key, out JsonElement property) || property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out int parsed) || parsed <= 0)
        {
            failure = Invalid("poll." + key, "must be a positive integer");
            return false;
        }

        value = parsed;
        return true;
    }

    private static NodeConfigurationResult Invalid(string field, string detail) =>
        NodeConfigurationResult.Fail(NodeConfigurationError.InvalidField, field, detail);
}
