using Zyggy.Core.Memory;

namespace Zyggy.Core.LinkedIn;

/// <summary>How <c>zyggy linkedin mcp-server</c> starts: an exit code and its stderr line, or whether the one tool is offered.</summary>
internal sealed record ServerStart(int? Exit, string? Message, bool OfferTool, LinkedInConfiguration? Config);

/// <summary>
/// The server's start rule (spec 36 AC-1, AC-6), decided before any protocol code runs: an unattended run (<c>ZYGGY_HOOKS=off</c>) is
/// refused with exit 5 before anything else is read; a configuration or principal error exits 3; with <c>post</c> not enabled the server
/// starts and offers no tool.
/// </summary>
internal static class LinkedInServerStart
{
    public const string UnattendedRefusal = "linkedin: refused in an unattended run";

    public static ServerStart Decide(IReadOnlyDictionary<string, string?> environment, Func<string, TimeZoneInfo>? findTimeZone = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (environment.TryGetValue("ZYGGY_HOOKS", out var hooks) && hooks == "off")
        {
            return new ServerStart(5, UnattendedRefusal, false, null);
        }

        var load = LinkedInConfiguration.Load(environment);
        if (load.Configuration is not { } config)
        {
            return new ServerStart(3, "linkedin: " + load.Error, false, null);
        }

        var memory = MemoryEnvironment.Resolve(environment, findTimeZone);
        return memory.Error is not null
            ? new ServerStart(3, "linkedin: configuration error: " + memory.Error, false, null)
            : new ServerStart(null, null, config.PostEnabled, config);
    }
}
