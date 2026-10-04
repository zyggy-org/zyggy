using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Zyggy.Core.Memory;

/// <summary>
/// The name of a memory file without <c>.md</c>, unique across the principal's tree so <c>[[slug]]</c> links stay
/// unambiguous (spec 28 Memory layout).
/// </summary>
/// <remarks>Syntax: <c>^[a-z0-9][a-z0-9-]{0,59}$</c>; a name starting with <c>_</c> is never a memory file.</remarks>
public sealed partial record Slug
{
    private Slug(string value) => Value = value;

    /// <summary>Gets the slug.</summary>
    public string Value { get; }

    /// <summary>Parses a slug.</summary>
    /// <param name="value">The text.</param>
    /// <returns>The slug.</returns>
    /// <exception cref="FormatException">Thrown when <paramref name="value"/> is not a valid slug.</exception>
    public static Slug Parse(string value) =>
        TryParse(value, out var result) ? result : throw new FormatException("A slug must match ^[a-z0-9][a-z0-9-]{0,59}$.");

    /// <summary>Tries to parse a slug.</summary>
    /// <param name="value">The text, or <see langword="null"/>.</param>
    /// <param name="result">The slug when valid; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is valid.</returns>
    public static bool TryParse(string? value, [NotNullWhen(true)] out Slug? result)
    {
        result = value is not null && Syntax().IsMatch(value) ? new Slug(value) : null;
        return result is not null;
    }

    /// <summary>Returns the slug.</summary>
    /// <returns><see cref="Value"/>.</returns>
    public override string ToString() => Value;

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,59}$", RegexOptions.CultureInvariant)]
    private static partial Regex Syntax();
}
