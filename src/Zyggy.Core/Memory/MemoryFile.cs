namespace Zyggy.Core.Memory;

/// <summary>A memory file: the §7 front matter (name, description, aliases, updated, any other keys) and its body lines.</summary>
/// <param name="Name">The <c>name</c> key.</param>
/// <param name="Description">The <c>description</c> key.</param>
/// <param name="Aliases">The <c>aliases</c> key.</param>
/// <param name="Updated">The <c>updated</c> key (<c>YYYY-MM-DD</c>).</param>
/// <param name="UnknownKeys">Any other scalar keys, kept in order.</param>
/// <param name="HasFrontMatter">Whether the file has a front matter block.</param>
/// <param name="BodyLines">The body, one entry per line, without line endings.</param>
public sealed record MemoryFile(
    string? Name,
    string? Description,
    IReadOnlyList<string> Aliases,
    DateOnly? Updated,
    IReadOnlyDictionary<string, string> UnknownKeys,
    bool HasFrontMatter,
    IReadOnlyList<string> BodyLines);
