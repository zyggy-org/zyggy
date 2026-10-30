using Zyggy.Core.Configuration;

namespace Zyggy.Core.Memory;

/// <summary>Reads Central's pickup configuration from the node's environment (spec 13 AC-16), with the dream's rules and messages.</summary>
public static class PickupConfiguration
{
    /// <summary>
    /// Loads <c>ZYGGY_MEMORY_ROOT</c>, <c>ZYGGY_TENANT</c> (which must be <paramref name="node"/>'s tenant), <c>ZYGGY_USER</c>,
    /// <c>ZYGGY_TIMEZONE</c> (UTC when absent) and the secret patterns (<c>ZYGGY_SECRET_PATTERNS</c>, else from
    /// <c>ZYGGY_INSTANCE_DIR</c>; fail closed).
    /// </summary>
    /// <param name="environment">The node's environment.</param>
    /// <param name="node">The node's <c>node.json</c>.</param>
    /// <returns>The configuration, or the first error.</returns>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    public static PickupConfigurationResult Load(IReadOnlyDictionary<string, string?> environment, NodeConfiguration node)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(node);
        MemoryEnvironmentResult memory = MemoryEnvironment.Resolve(environment);
        if (memory.Error is { } error)
        {
            return new PickupConfigurationResult(null, null, error);
        }

        if (memory.Paths!.Principal.Tenant != node.Tenant)
        {
            return new PickupConfigurationResult(null, null, $"ZYGGY_TENANT '{memory.Paths.Principal.Tenant.Value}' is not node.json's tenant '{node.Tenant.Value}'");
        }

        (string? path, string? patternsError) = SecretPatternsLocation.ResolveForInstance(environment);
        if (path is null)
        {
            return new PickupConfigurationResult(null, null, patternsError);
        }

        SecretPatternsLoad patterns = SecretPatterns.Load(path);
        return patterns.Patterns is null
            ? new PickupConfigurationResult(null, null, patterns.Error)
            : PickupConfigurationResult.For(memory.Paths, memory.TimeZone!, patterns.Patterns);
    }
}

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
