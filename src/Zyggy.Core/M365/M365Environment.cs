using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Zyggy.Core.M365;

/// <summary>The outcome of <see cref="M365Environment.Load"/>.</summary>
internal sealed record M365EnvironmentLoad(M365Environment? Environment, string? Error);

/// <summary>
/// Where the m365 verbs find their instance (spec 33 Configuration): <c>ZYGGY_INSTANCE_DIR</c>, else <c>$CLAUDE_PROJECT_DIR/instance</c>;
/// the checkout is its parent. <c>ZYGGY_M365_CONFIG</c>, <c>ZYGGY_M365_SETTINGS</c> and <c>ZYGGY_SECRET_PATTERNS</c> override the
/// files derived from it.
/// </summary>
internal sealed partial record M365Environment(
    string InstanceDirectory,
    string Checkout,
    string ConfigPath,
    string SettingsPath,
    string SecretPatternsPath,
    M365Paths Paths)
{
    private const int DefaultPort = 47365;

    private static readonly string[] PrincipalKeys = ["ZYGGY_MEMORY_ROOT", "ZYGGY_TENANT", "ZYGGY_USER", "ZYGGY_TIMEZONE"];

    public static M365EnvironmentLoad Load(IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        string? Get(string key) => Value(environment, key);

        var instance = Get("ZYGGY_INSTANCE_DIR") ?? (Get("CLAUDE_PROJECT_DIR") is { } project ? Path.Join(project, "instance") : null);
        if (instance is null)
        {
            return new M365EnvironmentLoad(null, "configuration error: ZYGGY_INSTANCE_DIR is not set (and no CLAUDE_PROJECT_DIR to derive it from)");
        }

        var checkout = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(instance)))!;
        return new M365EnvironmentLoad(
            new M365Environment(
                instance,
                checkout,
                Get("ZYGGY_M365_CONFIG") ?? Path.Join(instance, "m365.json"),
                Get("ZYGGY_M365_SETTINGS") ?? Path.Join(checkout, ".claude", "settings.local.json"),
                Get("ZYGGY_SECRET_PATTERNS") ?? Path.Join(checkout, ".claude", "hooks", "secret-patterns.txt"),
                new M365Paths(environment)),
            null);
    }

    /// <summary><c>zy_m365_port</c>: <c>ZYGGY_M365_PORT</c> (default 47365), 4–5 digits within 1024–65535.</summary>
    public static int? Port(IReadOnlyDictionary<string, string?> environment, out string? error)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var value = Value(environment, "ZYGGY_M365_PORT") ?? DefaultPort.ToString(CultureInfo.InvariantCulture);
        if (PortDigits().IsMatch(value) && int.Parse(value, CultureInfo.InvariantCulture) is >= 1024 and <= 65535 and var port)
        {
            error = null;
            return port;
        }

        error = $"configuration error: ZYGGY_M365_PORT '{value}' is not a port in 1024..65535";
        return null;
    }

    /// <summary>
    /// <c>zy_m365_principal_from_settings</c> (the backfills, started from a plain shell): each of <c>ZYGGY_MEMORY_ROOT</c>,
    /// <c>ZYGGY_TENANT</c>, <c>ZYGGY_USER</c>, <c>ZYGGY_TIMEZONE</c> that is unset or empty is taken from the settings file's
    /// <c>env</c> object when it holds a non-empty string there. A set variable wins; no other key is read; any problem changes nothing.
    /// </summary>
    public static Dictionary<string, string?> PrincipalFromSettings(IReadOnlyDictionary<string, string?> environment, string settingsPath)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var merged = new Dictionary<string, string?>(environment, StringComparer.Ordinal);
        try
        {
            if (!File.Exists(settingsPath))
            {
                return merged;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("env", out var env)
                || env.ValueKind != JsonValueKind.Object)
            {
                return merged;
            }

            foreach (var key in PrincipalKeys)
            {
                if (Value(environment, key) is null
                    && env.TryGetProperty(key, out var value)
                    && value.ValueKind == JsonValueKind.String
                    && value.GetString() is { Length: > 0 } text)
                {
                    merged[key] = text;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // As the shell: no readable file or no JSON changes nothing.
        }

        return merged;
    }

    private static string? Value(IReadOnlyDictionary<string, string?> environment, string key) =>
        environment.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value) ? value : null;

    [GeneratedRegex(@"\A[0-9]{4,5}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PortDigits();
}
