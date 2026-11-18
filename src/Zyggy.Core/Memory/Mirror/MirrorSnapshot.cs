namespace Zyggy.Core.Memory.Mirror;

/// <summary>
/// The files one machine's mirror holds after a publish (spec 47): mirror path (<c>/</c> separators) → bytes, <c>MIRROR.md</c>
/// included, plus the mirror paths withheld whole because a line hit a secret or contact-detail pattern.
/// </summary>
/// <param name="Files">The snapshot's files, sorted by path (ordinal).</param>
/// <param name="Withheld">The withheld files' mirror paths, sorted (ordinal). Never their content.</param>
internal sealed record MirrorSnapshot(IReadOnlyDictionary<string, byte[]> Files, IReadOnlyList<string> Withheld);

/// <summary>What a new snapshot changes against the mirror's previous files (spec 47 AC-4, AC-11).</summary>
/// <param name="Added">Paths new in the snapshot.</param>
/// <param name="Changed">Paths in both with different bytes.</param>
/// <param name="Removed">Paths in the previous files and no longer in the snapshot (withheld ones included).</param>
/// <param name="Withheld">The snapshot's withheld paths.</param>
internal sealed record MirrorChanges(IReadOnlyList<string> Added, IReadOnlyList<string> Changed, IReadOnlyList<string> Removed, IReadOnlyList<string> Withheld)
{
    /// <summary>Gets a value indicating whether nothing would be committed (withheld files alone change nothing).</summary>
    public bool IsEmpty => Added.Count == 0 && Changed.Count == 0 && Removed.Count == 0;

    /// <summary>Compares the mirror's <paramref name="previous"/> files with <paramref name="next"/>; every list sorted (ordinal).</summary>
    public static MirrorChanges Between(IReadOnlyDictionary<string, byte[]> previous, MirrorSnapshot next)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(next);
        var added = next.Files.Keys.Where(k => !previous.ContainsKey(k)).Order(StringComparer.Ordinal).ToList();
        var changed = next.Files
            .Where(f => previous.TryGetValue(f.Key, out var old) && !old.AsSpan().SequenceEqual(f.Value))
            .Select(f => f.Key)
            .Order(StringComparer.Ordinal)
            .ToList();
        var removed = previous.Keys.Where(k => !next.Files.ContainsKey(k)).Order(StringComparer.Ordinal).ToList();
        return new MirrorChanges(added, changed, removed, next.Withheld);
    }
}
