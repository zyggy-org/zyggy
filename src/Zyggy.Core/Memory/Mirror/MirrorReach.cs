namespace Zyggy.Core.Memory.Mirror;

/// <summary>
/// What one machine may read of a principal's memory (spec 47 "What each machine may read"): paths relative to
/// <c>&lt;tenant&gt;/&lt;user&gt;/</c>, a folder ending in <c>/</c> or a single file. Whole files and folders only.
/// </summary>
internal sealed record MirrorReach
{
    private MirrorReach(IReadOnlyList<string> paths) => Paths = paths;

    /// <summary>
    /// Gets the reach of a machine that is not a work machine: the identity files, <c>private/</c>, <c>business/</c>, <c>daily/</c> and
    /// the sidecars under <c>archive/</c>.
    /// </summary>
    public static MirrorReach Default { get; } =
        new(["profile.md", "preferences.md", "agents.md", "private/", "business/", "daily/", "archive/"]);

    /// <summary>Gets the paths, in the order given.</summary>
    public IReadOnlyList<string> Paths { get; }

    /// <summary>Creates a reach from configured paths (<c>mirror.targets[].paths</c>).</summary>
    /// <param name="paths">Relative paths; a folder ends in <c>/</c>.</param>
    /// <returns>The reach.</returns>
    public static MirrorReach Of(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return new([.. paths]);
    }

    /// <summary>Whether <paramref name="relativePath"/> (<c>/</c> separators) lies inside one of the reach's paths.</summary>
    public bool Includes(string relativePath) => Paths.Any(p => Covers(p, relativePath));

    /// <summary>Whether <paramref name="inner"/> lies inside <paramref name="outer"/>: the same file, or under the same folder.</summary>
    public static bool Covers(string outer, string inner)
    {
        ArgumentNullException.ThrowIfNull(outer);
        ArgumentNullException.ThrowIfNull(inner);
        return outer.EndsWith('/') ? inner.StartsWith(outer, StringComparison.Ordinal) : inner == outer;
    }

    /// <inheritdoc/>
    public bool Equals(MirrorReach? other) => other is not null && Paths.SequenceEqual(other.Paths, StringComparer.Ordinal);

    /// <inheritdoc/>
    public override int GetHashCode() => Paths.Count;
}
