using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Memory;

/// <summary>The principal's memory tree and local time zone, or the configuration error that prevents them.</summary>
internal sealed record MemoryEnvironmentResult(MemoryPaths? Paths, TimeZoneInfo? TimeZone, string? Error);

/// <summary>
/// <c>zy_require_config</c> of the template's <c>lib.sh</c> for the script verbs: <c>ZYGGY_MEMORY_ROOT</c>, <c>ZYGGY_TENANT</c>,
/// <c>ZYGGY_USER</c> set; the root and the principal directory exist; <c>ZYGGY_TIMEZONE</c> (default UTC) is a known zone.
/// The messages are the script's, without the <c>&lt;script&gt;: configuration error: </c> prefix.
/// </summary>
internal static class MemoryEnvironment
{
    public static MemoryEnvironmentResult Resolve(IReadOnlyDictionary<string, string?> environment, Func<string, TimeZoneInfo>? findTimeZone = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        string? Get(string key) => environment.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value) ? value : null;

        foreach (var key in new[] { "ZYGGY_MEMORY_ROOT", "ZYGGY_TENANT", "ZYGGY_USER" })
        {
            if (Get(key) is null)
            {
                return Fail($"{key} is not set");
            }
        }

        var root = Get("ZYGGY_MEMORY_ROOT")!;
        if (!Directory.Exists(root))
        {
            return Fail($"memory root {root} does not exist (ZYGGY_MEMORY_ROOT)");
        }

        if (!TenantId.TryParse(Get("ZYGGY_TENANT"), out var tenant))
        {
            return Fail($"ZYGGY_TENANT '{Get("ZYGGY_TENANT")}' is not a valid label");
        }

        if (!UserId.TryParse(Get("ZYGGY_USER"), out var user))
        {
            return Fail($"ZYGGY_USER '{Get("ZYGGY_USER")}' is not a valid label");
        }

        var paths = new MemoryPaths(root, new Principal(tenant, user));
        if (!Directory.Exists(paths.PrincipalDirectory))
        {
            return Fail($"memory directory {paths.PrincipalDirectory} does not exist (ZYGGY_TENANT/ZYGGY_USER)");
        }

        var zoneId = Get("ZYGGY_TIMEZONE") ?? "UTC";
        try
        {
            var zone = zoneId == "UTC" ? TimeZoneInfo.Utc : (findTimeZone ?? TimeZoneInfo.FindSystemTimeZoneById)(zoneId);
            return new MemoryEnvironmentResult(paths, zone, null);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return Fail($"ZYGGY_TIMEZONE '{zoneId}' is not a known time zone");
        }
    }

    private static MemoryEnvironmentResult Fail(string message) => new(null, null, message);
}
