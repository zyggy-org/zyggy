namespace Zyggy.Core.Registry;

/// <summary>
/// One subagent or skill of a registered project (founding spec §4 "Registry file", O24): its name and the <c>description</c>
/// front matter of its file.
/// </summary>
/// <param name="Name">The agent or skill name: the file name without <c>.md</c>, or the skill's folder name.</param>
/// <param name="Description">The description, control characters removed, at most 150 characters; empty when absent.</param>
public sealed record RegistryEntry(string Name, string Description)
{
    /// <summary>The longest description a registry file carries (O24).</summary>
    public const int MaxDescriptionLength = 150;
}
