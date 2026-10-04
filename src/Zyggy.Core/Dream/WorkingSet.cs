namespace Zyggy.Core.Dream;

/// <summary>
/// The proposed new contents of the principal's tree over a snapshot, kept in memory until every check has passed
/// (spec 28 "No half-commit"). A <see langword="null"/> content means the file is deleted.
/// </summary>
internal sealed class WorkingSet
{
    private readonly Dictionary<string, string?> _changes;

    public WorkingSet(MemorySnapshot snapshot)
        : this(snapshot, new Dictionary<string, string?>(StringComparer.Ordinal))
    {
    }

    private WorkingSet(MemorySnapshot snapshot, Dictionary<string, string?> changes)
    {
        Snapshot = snapshot;
        _changes = changes;
    }

    public MemorySnapshot Snapshot { get; }

    /// <summary>Paths whose content differs from the snapshot (created, edited or deleted), in ordinal order.</summary>
    public IReadOnlyList<string> ChangedPaths =>
        _changes.Where(c => !Snapshot.Files.TryGetValue(c.Key, out var file) ? c.Value is not null : c.Value != file.Text)
            .Select(c => c.Key)
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>Every path that exists in the working set.</summary>
    public IEnumerable<string> Paths =>
        Snapshot.Files.Keys.Where(p => !_changes.TryGetValue(p, out var text) || text is not null)
            .Concat(_changes.Where(c => c.Value is not null && !Snapshot.Files.ContainsKey(c.Key)).Select(c => c.Key));

    public string? Text(string relativePath) =>
        _changes.TryGetValue(relativePath, out var text) ? text : Snapshot.Files.TryGetValue(relativePath, out var file) ? file.Text : null;

    public bool Exists(string relativePath) => Text(relativePath) is not null;

    public bool ExistedBefore(string relativePath) => Snapshot.Files.ContainsKey(relativePath);

    public void Write(string relativePath, string text) => _changes[relativePath] = text;

    public void Delete(string relativePath) => _changes[relativePath] = null;

    public WorkingSet Clone() => new(Snapshot, new Dictionary<string, string?>(_changes, StringComparer.Ordinal));

    /// <summary>Takes over every change of <paramref name="other"/> (a clone of this set that passed its checks).</summary>
    public void Adopt(WorkingSet other)
    {
        ArgumentNullException.ThrowIfNull(other);
        _changes.Clear();
        foreach (var (path, text) in other._changes)
        {
            _changes[path] = text;
        }
    }
}
