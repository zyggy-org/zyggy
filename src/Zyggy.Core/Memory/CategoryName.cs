using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Zyggy.Core.Memory;

/// <summary>A memory category directory on one side, for example <c>areas</c> or <c>clients</c> (spec 28 Memory layout).</summary>
/// <remarks>Syntax: <c>^[a-z][a-z0-9-]{1,30}$</c>.</remarks>
public sealed partial record CategoryName
{
    private CategoryName(string value) => Value = value;

    /// <summary>Gets the category name.</summary>
    public string Value { get; }

    /// <summary>Parses a category name.</summary>
    /// <param name="value">The text.</param>
    /// <returns>The category name.</returns>
    /// <exception cref="FormatException">Thrown when <paramref name="value"/> is not a valid category name.</exception>
    public static CategoryName Parse(string value) =>
        TryParse(value, out var result) ? result : throw new FormatException("A category name must match ^[a-z][a-z0-9-]{1,30}$.");

    /// <summary>Tries to parse a category name.</summary>
    /// <param name="value">The text, or <see langword="null"/>.</param>
    /// <param name="result">The category name when valid; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is valid.</returns>
    public static bool TryParse(string? value, [NotNullWhen(true)] out CategoryName? result)
    {
        result = value is not null && Syntax().IsMatch(value) ? new CategoryName(value) : null;
        return result is not null;
    }

    /// <summary>Returns the category name.</summary>
    /// <returns><see cref="Value"/>.</returns>
    public override string ToString() => Value;

    [GeneratedRegex("^[a-z][a-z0-9-]{1,30}$", RegexOptions.CultureInvariant)]
    private static partial Regex Syntax();
}
