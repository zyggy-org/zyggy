using System.Text.Json;

namespace Zyggy.Core.Memory;

/// <summary>
/// The principal fallback for a process started without the memory variables (spec 33, moved here by spec 11 D1 so the m365 verbs and
/// the Hub share it): each of <c>ZYGGY_MEMORY_ROOT</c>, <c>ZYGGY_TENANT</c>, <c>ZYGGY_USER</c>, <c>ZYGGY_TIMEZONE</c> that is unset or
/// empty is taken from the settings file's <c>env</c> object when it holds a non-empty string there. A set variable wins; no other key
/// is read; any problem changes nothing.
/// </summary>
internal static class PrincipalFromSettings
{
    private static readonly string[] PrincipalKeys = ["ZYGGY_MEMORY_ROOT", "ZYGGY_TENANT", "ZYGGY_USER", "ZYGGY_TIMEZONE"];

    /// <summary>The settings file of a Claude Code project: <c>&lt;project&gt;/.claude/settings.local.json</c>.</summary>
    public static string SettingsPath(string projectDirectory) => Path.Join(projectDirectory, ".claude", "settings.local.json");

    /// <summary>Returns a copy of <paramref name="environment"/> with the unset principal keys filled from the settings file.</summary>
    public static Dictionary<string, string?> Apply(IReadOnlyDictionary<string, string?> environment, string settingsPath)
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
}
