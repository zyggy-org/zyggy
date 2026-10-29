namespace Zyggy.Core.Memory;

/// <summary>
/// What Central's pickup needs from its environment (spec 13, Configuration): the memory principal's paths, the local zone for
/// <c>updated</c> and the secret patterns; or the one-line error that stops Central's node at start-up (exit 3).
/// </summary>
/// <param name="Paths">The memory paths; <see langword="null"/> on error.</param>
/// <param name="Zone">The zone of <c>ZYGGY_TIMEZONE</c>; <see langword="null"/> on error.</param>
/// <param name="Error">The configuration error; <see langword="null"/> on success.</param>
public sealed record PickupConfigurationResult(MemoryPaths? Paths, TimeZoneInfo? Zone, string? Error)
{
    /// <summary>Gets the loaded secret patterns; <see langword="null"/> on error.</summary>
    internal SecretPatterns? Patterns { get; init; }

    /// <summary>A loaded configuration.</summary>
    internal static PickupConfigurationResult For(MemoryPaths paths, TimeZoneInfo zone, SecretPatterns patterns) =>
        new(paths, zone, null) { Patterns = patterns };
}
