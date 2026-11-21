using Zyggy.Core.Health;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Registry;

/// <summary>
/// One machine's liveness as its registry file tells it (spec 59 slice 7, <c>zyggy status</c>): when it was last seen, which
/// <c>zyggy</c> it runs, its health, and whether it is silent past <c>discovery.lastSeenHours</c>.
/// </summary>
/// <param name="Machine">The machine.</param>
/// <param name="LastSeen">The file's <c>last_seen</c>; <see langword="null"/> when the key is absent.</param>
/// <param name="Version">The file's <c>version</c>; <see langword="null"/> for a file written before spec 59.</param>
/// <param name="Health">The file's <c>health</c>, when present.</param>
/// <param name="Stale"><see langword="true"/> when <see cref="RegistryDocument.IsStaleAt"/> says so.</param>
public sealed record MachineLiveness(MachineName Machine, DateTimeOffset? LastSeen, string? Version, HealthLevel? Health, bool Stale)
{
    /// <summary>Reads the liveness of <paramref name="document"/> at <paramref name="now"/>.</summary>
    /// <param name="document">The registry file.</param>
    /// <param name="now">The instant to judge at.</param>
    /// <param name="staleAfter">The threshold, <c>discovery.lastSeenHours</c>.</param>
    /// <returns>The liveness.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="document"/> is null.</exception>
    public static MachineLiveness Of(RegistryDocument document, DateTimeOffset now, TimeSpan staleAfter)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new MachineLiveness(document.Machine, document.LastSeen, document.Version, document.Health, document.IsStaleAt(now, staleAfter));
    }
}
