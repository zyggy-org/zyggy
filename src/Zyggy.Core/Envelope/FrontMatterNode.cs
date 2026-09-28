using System.Collections.Immutable;

namespace Zyggy.Core.Envelope;

/// <summary>
/// A node of the parsed front matter: a scalar, a sequence or a mapping (founding spec §4 "Canonical form, normative
/// details"). The tree keeps every field, known or unknown, and is what the signature covers. The hierarchy is closed.
/// </summary>
public abstract record FrontMatterNode
{
    private protected FrontMatterNode()
    {
    }
}

/// <summary>A scalar kept as its parsed text, whatever its original style (plain, single- or double-quoted).</summary>
/// <param name="Value">The scalar text exactly as parsed.</param>
public sealed record FrontMatterScalar(string Value) : FrontMatterNode;

/// <summary>A sequence (flow or block in the file). Equality is item by item.</summary>
public sealed record FrontMatterSequence : FrontMatterNode
{
    /// <summary>Creates a sequence.</summary>
    /// <param name="items">The items in file order.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="items"/> or one of its items is null.</exception>
    public FrontMatterSequence(IEnumerable<FrontMatterNode> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        ImmutableArray<FrontMatterNode> copy = [.. items];
        foreach (FrontMatterNode item in copy)
        {
            ArgumentNullException.ThrowIfNull(item, nameof(items));
        }

        Items = copy;
    }

    /// <summary>Gets the items in file order (sequence order is data; only mapping keys are sorted).</summary>
    public IReadOnlyList<FrontMatterNode> Items { get; }

    /// <summary>Determines whether both sequences hold equal items in the same order.</summary>
    /// <param name="other">The other sequence.</param>
    /// <returns><see langword="true"/> when the sequences are structurally equal.</returns>
    public bool Equals(FrontMatterSequence? other) => other is not null && Items.SequenceEqual(other.Items);

    /// <summary>Returns a structural hash code.</summary>
    /// <returns>The hash code.</returns>
    public override int GetHashCode()
    {
        var hash = default(HashCode);
        foreach (FrontMatterNode item in Items)
        {
            hash.Add(item);
        }

        return hash.ToHashCode();
    }
}

/// <summary>
/// A mapping with unique string keys. Enumeration order is ordinal key order, the canonical order (§4), by construction.
/// Equality is entry by entry.
/// </summary>
public sealed record FrontMatterMapping : FrontMatterNode
{
    /// <summary>Creates a mapping; the entries are copied into ordinal key order.</summary>
    /// <param name="entries">The entries.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="entries"/> or one of its values is null.</exception>
    public FrontMatterMapping(IEnumerable<KeyValuePair<string, FrontMatterNode>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ImmutableSortedDictionary<string, FrontMatterNode> copy = ImmutableSortedDictionary.CreateRange(StringComparer.Ordinal, entries);
        foreach (FrontMatterNode value in copy.Values)
        {
            ArgumentNullException.ThrowIfNull(value, nameof(entries));
        }

        Entries = copy;
    }

    /// <summary>Gets the entries, enumerated in ordinal key order.</summary>
    public IReadOnlyDictionary<string, FrontMatterNode> Entries { get; }

    /// <summary>Determines whether both mappings hold the same keys with equal values.</summary>
    /// <param name="other">The other mapping.</param>
    /// <returns><see langword="true"/> when the mappings are structurally equal.</returns>
    public bool Equals(FrontMatterMapping? other) =>
        other is not null
        && Entries.Count == other.Entries.Count
        && Entries.All(e => other.Entries.TryGetValue(e.Key, out FrontMatterNode? value) && e.Value.Equals(value));

    /// <summary>Returns a structural hash code.</summary>
    /// <returns>The hash code.</returns>
    public override int GetHashCode()
    {
        var hash = default(HashCode);
        foreach (KeyValuePair<string, FrontMatterNode> entry in Entries)
        {
            hash.Add(entry.Key, StringComparer.Ordinal);
            hash.Add(entry.Value);
        }

        return hash.ToHashCode();
    }
}
