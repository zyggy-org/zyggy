using System.Diagnostics.CodeAnalysis;

namespace Zyggy.Core.Registry;

/// <summary>
/// The node's own latest scan, held in memory (founding spec §6 step 1 as amended by A14-3): the only source pre-flight and the
/// dispatcher resolve a project name from — never the bus copy of the registry file. Thread-safe; empty until the first scan.
/// </summary>
public sealed class RegistrySnapshot
{
    private volatile RegistryDocument? _current;

    /// <summary>Gets the latest scanned document, or <see langword="null"/> before the first scan.</summary>
    public RegistryDocument? Current => _current;

    /// <summary>Replaces the snapshot with a newer scan.</summary>
    /// <param name="document">The scanned document.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="document"/> is null.</exception>
    public void Replace(RegistryDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        _current = document;
    }

    /// <summary>
    /// Resolves a registry name to the folder this node discovered for it. Only a routable label (<c>[a-z0-9-]</c>) can resolve; the
    /// lookup is ordinal; an entry without a path (a <c>detail: names</c> scan) never resolves; nothing resolves before the first scan.
    /// </summary>
    /// <param name="name">The job's <c>project</c> value.</param>
    /// <param name="path">The project folder in the operating system's form, when found.</param>
    /// <returns><see langword="true"/> when <paramref name="name"/> names a project of the latest scan that carries a path.</returns>
    public bool TryResolve(string name, [NotNullWhen(true)] out string? path)
    {
        path = null;
        if (string.IsNullOrEmpty(name) || !ProjectName.IsLabel(name) || _current is not { } current)
        {
            return false;
        }

        foreach (RegistryProject project in current.Projects)
        {
            if (string.Equals(project.Name, name, StringComparison.Ordinal) && project.Path is { } found)
            {
                path = Path.GetFullPath(found);
                return true;
            }
        }

        return false;
    }
}
