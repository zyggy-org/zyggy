using Zyggy.Core.Memory;

namespace Zyggy.Core.Hub;

/// <summary>
/// The Hub's start rule (spec 11 "Principal resolution", D1), decided once before any protocol line: the principal and time zone come
/// from the <c>ZYGGY_*</c> variables of the process environment, each unset one filled from
/// <c>$CLAUDE_PROJECT_DIR/.claude/settings.local.json</c>; then the secret patterns are loaded. Anything missing → exit 3 with one line.
/// The principal is never read from a request.
/// </summary>
internal static class HubStartup
{
    private const string NoPatternLocation =
        "ZYGGY_SECRET_PATTERNS is not set (and no ZYGGY_INSTANCE_DIR or CLAUDE_PROJECT_DIR to derive it from)";

    public static HubStart Decide(IReadOnlyDictionary<string, string?> environment, Func<string, TimeZoneInfo> findTimeZone)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(findTimeZone);
        IReadOnlyDictionary<string, string?> merged = environment.TryGetValue("CLAUDE_PROJECT_DIR", out var project) && !string.IsNullOrEmpty(project)
            ? PrincipalFromSettings.Apply(environment, PrincipalFromSettings.SettingsPath(project))
            : environment;

        var memory = MemoryEnvironment.Resolve(merged, findTimeZone);
        if (memory.Error is not null)
        {
            return Refuse(memory.Error);
        }

        if (SecretPatternsLocation.Resolve(merged) is not { } location)
        {
            return Refuse(NoPatternLocation);
        }

        var load = SecretPatterns.Load(location);
        if (load.Patterns is null)
        {
            return Refuse(load.Error!);
        }

        var hooksOff = merged.TryGetValue("ZYGGY_HOOKS", out var hooks) && hooks == "off";
        return new HubStart(null, null, memory.Paths, memory.TimeZone, load.Patterns, hooksOff);
    }

    private static HubStart Refuse(string message) =>
        new(HubVerb.Configuration, "hub: configuration error: " + message, null, null, null, HooksOff: false);
}
