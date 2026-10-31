using Microsoft.Extensions.DependencyInjection;

namespace Zyggy.Core.Memory;

/// <summary>
/// The one public way a host registers the secret patterns (spec 12 A1): resolves <c>secret-patterns.txt</c> as the memory verbs do
/// (<c>ZYGGY_SECRET_PATTERNS</c>, else derived from <c>ZYGGY_INSTANCE_DIR</c> or <c>CLAUDE_PROJECT_DIR</c>), loads it and registers
/// the patterns as a singleton — or registers nothing and returns why (fail closed).
/// </summary>
public static class SecretPatternsRegistration
{
    internal const string NotSet = "ZYGGY_SECRET_PATTERNS is not set (and no ZYGGY_INSTANCE_DIR or CLAUDE_PROJECT_DIR to derive it from)";

    /// <summary>Registers the secret patterns from the environment's location, or returns the reason they could not be read.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="environment">The process environment.</param>
    /// <returns><see langword="null"/> when registered; otherwise the load error, with nothing registered.</returns>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    public static string? AddSecretPatterns(this IServiceCollection services, IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(environment);
        var location = SecretPatternsLocation.Resolve(environment);
        if (location is null)
        {
            return NotSet;
        }

        var load = SecretPatterns.Load(location);
        if (load.Patterns is null)
        {
            return load.Error;
        }

        services.AddSingleton(load.Patterns);
        return null;
    }
}
