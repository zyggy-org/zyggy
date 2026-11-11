namespace Zyggy.Core.Registry;

/// <summary>
/// One Claude Code project of a machine's registry file (founding spec §4 "Registry file"). Equality is structural, lists
/// compared element by element.
/// </summary>
/// <param name="Name">The derived routable label (<c>[a-z0-9-]</c>).</param>
/// <param name="Path">The absolute folder path with <c>/</c> separators; <see langword="null"/> under <see cref="DiscoveryDetail.Names"/>.</param>
/// <param name="DefaultBranch">The default branch; <see langword="null"/> when unknown (key omitted).</param>
/// <param name="Agents">The subagents, ordered by name; empty under <see cref="DiscoveryDetail.Names"/>.</param>
/// <param name="Skills">The skills, ordered by name; empty under <see cref="DiscoveryDetail.Names"/>.</param>
/// <param name="Summary">The <c>CLAUDE.md</c> summary line; <see langword="null"/> when absent (key omitted).</param>
public sealed record RegistryProject(
    string Name,
    string? Path,
    string? DefaultBranch,
    IReadOnlyList<RegistryEntry> Agents,
    IReadOnlyList<RegistryEntry> Skills,
    string? Summary)
{
    /// <summary>Determines whether both projects are structurally equal.</summary>
    /// <param name="other">The other project.</param>
    /// <returns><see langword="true"/> when every field and every list item is equal.</returns>
    public bool Equals(RegistryProject? other) =>
        other is not null
        && string.Equals(Name, other.Name, StringComparison.Ordinal)
        && string.Equals(Path, other.Path, StringComparison.Ordinal)
        && string.Equals(DefaultBranch, other.DefaultBranch, StringComparison.Ordinal)
        && Agents.SequenceEqual(other.Agents)
        && Skills.SequenceEqual(other.Skills)
        && string.Equals(Summary, other.Summary, StringComparison.Ordinal);

    /// <summary>Returns a structural hash code.</summary>
    /// <returns>The hash code.</returns>
    public override int GetHashCode()
    {
        var hash = default(HashCode);
        hash.Add(Name, StringComparer.Ordinal);
        hash.Add(Path, StringComparer.Ordinal);
        hash.Add(DefaultBranch, StringComparer.Ordinal);
        foreach (RegistryEntry agent in Agents)
        {
            hash.Add(agent);
        }

        foreach (RegistryEntry skill in Skills)
        {
            hash.Add(skill);
        }

        hash.Add(Summary, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}
