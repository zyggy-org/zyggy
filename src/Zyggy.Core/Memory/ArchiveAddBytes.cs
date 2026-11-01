namespace Zyggy.Core.Memory;

/// <summary>
/// An item to file from bytes already obtained (spec 41: Central's pickup, decoded from a bus item or downloaded from OneDrive): the
/// same fields <c>archive add</c> takes, with the source file name the sender recorded in place of a source path.
/// </summary>
/// <param name="Project">The project.</param>
/// <param name="Name">The item's name.</param>
/// <param name="Description">The item's description.</param>
/// <param name="Slug">The item's slug.</param>
/// <param name="SourceName">The sender's source file name, kept in the sidecar; its extension says whether a text item is Markdown.</param>
/// <param name="Bytes">The item's bytes.</param>
internal sealed record ArchiveAddBytes(Slug Project, string Name, string Description, Slug Slug, string SourceName, byte[] Bytes);
