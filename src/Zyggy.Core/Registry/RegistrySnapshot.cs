using System.Diagnostics.CodeAnalysis;

namespace Zyggy.Core.Registry;

/// <summary>
/// The node's own latest scan, held in memory (founding spec §6 step 1 as amended by A14-3): the only source pre-flight and the
/// dispatcher resolve a project name from — never the bus copy of the registry file. Beside the scanned document it keeps the folder of
/// each project (spec 56), so a <c>detail: names</c> node, whose document carries no path, still resolves a name; the folders are never
/// part of <see cref="Current"/> and so never reach the published file. Thread-safe; empty until the first scan.
/// </summary>
public sealed class RegistrySnapshot
{
    private static readonly IReadOnlyDictionary<string, string> NoFolders = new Dictionary<string, string>(StringComparer.Ordinal);

    private volatile State? _state;

    /// <summary>Gets the latest scanned document, or <see langword="null"/> before the first scan.</summary>
    public RegistryDocument? Current => _state?.Document;

    /// <summary>Replaces the snapshot with a newer scan whose projects carry their own paths (a <c>detail: full</c> document).</summary>
    /// <param name="document">The scanned document.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="document"/> is null.</exception>
    public void Replace(RegistryDocument document) => Replace(document, NoFolders);

    /// <summary>
    /// Replaces the snapshot with a newer scan and the folder the scan found for each project (<see cref="ScanResult.Folders"/>), kept
    /// beside the document and never inside it (spec 56, founding spec §8).
    /// </summary>
    /// <param name="document">The scanned document, as published.</param>
    /// <param name="folders">The folder of each project, by registry name, in the operating system's form.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="document"/> or <paramref name="folders"/> is null.</exception>
    public void Replace(RegistryDocument document, IReadOnlyDictionary<string, string> folders)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(folders);
        _state = new State(document, folders);
    }

    /// <summary>
    /// Resolves a registry name to the folder this node discovered for it. Only a routable label (<c>[a-z0-9-]</c>) can resolve; the
    /// lookup is ordinal; the folder kept beside the document comes first, then the entry's own path; nothing resolves before the first scan.
    /// </summary>
    /// <param name="name">The job's <c>project</c> value.</param>
    /// <param name="path">The project folder in the operating system's form, when found.</param>
    /// <returns><see langword="true"/> when <paramref name="name"/> names a project of the latest scan whose folder is known.</returns>
    public bool TryResolve(string name, [NotNullWhen(true)] out string? path)
    {
        path = null;
        if (string.IsNullOrEmpty(name) || !ProjectName.IsLabel(name) || _state is not { } state)
        {
            return false;
        }

        if (state.Folders.TryGetValue(name, out string? folder))
        {
            path = Path.GetFullPath(folder);
            return true;
        }

        foreach (RegistryProject project in state.Document.Projects)
        {
            if (string.Equals(project.Name, name, StringComparison.Ordinal) && project.Path is { } found)
            {
                path = Path.GetFullPath(found);
                return true;
            }
        }

        return false;
    }

    /// <summary>One scan, swapped as a whole so a reader never sees a document with another scan's folders.</summary>
    private sealed record State(RegistryDocument Document, IReadOnlyDictionary<string, string> Folders);
}
