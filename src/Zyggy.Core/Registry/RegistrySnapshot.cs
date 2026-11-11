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
}
