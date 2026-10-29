using Zyggy.Core.Memory;

namespace Zyggy.Core.Hub;

/// <summary>
/// What the Hub starts with (spec 11 "Principal resolution"): either an exit code and its one-line message, or the principal's memory
/// paths, the time zone for dates, the secret patterns and whether hooks are off. Decided once, before any protocol line.
/// </summary>
/// <param name="Exit">The exit code when the Hub must not start; otherwise <see langword="null"/>.</param>
/// <param name="Message">The stderr line (without the <c>hub: </c> prefix) when the Hub must not start.</param>
/// <param name="Paths">The principal's memory paths.</param>
/// <param name="TimeZone">The zone of <c>ZYGGY_TIMEZONE</c>.</param>
/// <param name="Patterns">The secret patterns <c>remember</c> refuses.</param>
/// <param name="HooksOff">Whether <c>ZYGGY_HOOKS=off</c>: <c>remember</c> stores nothing.</param>
internal sealed record HubStart(int? Exit, string? Message, MemoryPaths? Paths, TimeZoneInfo? TimeZone, SecretPatterns? Patterns, bool HooksOff);
