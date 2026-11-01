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
        if (patterns.Patterns is null)
        {
            return new PickupConfigurationResult(null, null, patterns.Error);
        }

        // Spec 41 (plan A8): the archive part never stops start-up; without it every item waits.
        (ArchiveOptions? archive, string? archiveError) = LoadArchive(environment);
        return PickupConfigurationResult.For(memory.Paths, memory.TimeZone!, patterns.Patterns) with
        {
            Archive = archive,
            ArchiveError = archiveError,
            Environment = environment,
        };
    }

    private static (ArchiveOptions? Options, string? Error) LoadArchive(IReadOnlyDictionary<string, string?> environment)
    {
        bool Set(string key) => environment.TryGetValue(key, out string? value) && !string.IsNullOrEmpty(value);
        if (!Set("ZYGGY_ARCHIVE_CONFIG") && !Set("ZYGGY_INSTANCE_DIR"))
        {
            return (null, "archive configuration missing (ZYGGY_INSTANCE_DIR not set)");
        }

        ArchiveConfigurationResult result = ArchiveConfiguration.Load(environment, file => File.Exists(file) ? File.ReadAllText(file) : null);
        return result.Options is null ? (null, result.Error) : (result.Options, null);
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

    /// <summary>Gets the archive options (spec 41, plan A8); <see langword="null"/> when <see cref="ArchiveError"/> is set.</summary>
    internal ArchiveOptions? Archive { get; init; }

    /// <summary>Gets why the archive part did not load (spec 41, plan A8): every item then waits; contexts and reports are unaffected.</summary>
    public string? ArchiveError { get; init; }

    /// <summary>Gets the node environment the configuration was read from (the archive source deny list, spec 41).</summary>
    internal IReadOnlyDictionary<string, string?>? Environment { get; init; }

    /// <summary>A loaded configuration.</summary>
    internal static PickupConfigurationResult For(MemoryPaths paths, TimeZoneInfo zone, SecretPatterns patterns) =>
        new(paths, zone, null) { Patterns = patterns };
}
