namespace Zyggy.Core.Memory;

/// <summary>
/// The two sides of the owner's memory (spec 28, OQ-1). There is deliberately no <c>work</c> side: <c>work/</c> is the
/// employer laptop's memory, which never reaches Central (founding spec §8).
/// </summary>
public enum MemorySide
{
    /// <summary>Private life (<c>private/</c>).</summary>
    Private,

    /// <summary>The owner's professional life (<c>business/</c>).</summary>
    Business,
}

/// <summary>The single mapping between <see cref="MemorySide"/> and its directory name.</summary>
public static class MemorySideWire
{
    /// <summary>Returns the directory name of a side.</summary>
    /// <param name="side">The side.</param>
    /// <returns><c>private</c> or <c>business</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for an undefined member.</exception>
    public static string ToWire(MemorySide side) => side switch
    {
        MemorySide.Private => "private",
        MemorySide.Business => "business",
        _ => throw new ArgumentOutOfRangeException(nameof(side)),
    };

    /// <summary>Maps a directory name to a side.</summary>
    /// <param name="wire">The directory name.</param>
    /// <param name="side">The side when recognised.</param>
    /// <returns><see langword="true"/> for exactly <c>private</c> or <c>business</c>.</returns>
    public static bool TryFromWire(string? wire, out MemorySide side)
    {
        foreach (var member in Enum.GetValues<MemorySide>())
        {
            if (string.Equals(ToWire(member), wire, StringComparison.Ordinal))
            {
                side = member;
                return true;
            }
        }

        side = default;
        return false;
    }
}
