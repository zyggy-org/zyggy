namespace Zyggy.Core.Memory;

/// <summary>A section of the memory digest a session loads at start (founding spec §7 Context loading).</summary>
public enum DigestSection
{
    /// <summary><c>profile.md</c> and <c>preferences.md</c>.</summary>
    Identity,

    /// <summary><c>agents.md</c>, one line per category, then file lines by <c>updated</c> descending.</summary>
    Index,

    /// <summary>The last seven <c>daily/YYYY-MM-DD.md</c> files.</summary>
    Daily,
}
