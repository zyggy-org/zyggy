using System.Diagnostics.CodeAnalysis;

namespace Zyggy.Core.Tenancy;

/// <summary>
/// Identifies a person inside a tenant (founding spec §14); memory lives under <c>memory/&lt;tenant&gt;/&lt;user&gt;/</c> (§7).
/// </summary>
/// <remarks>Syntax: a lowercase label <c>[a-z0-9-]</c>, 1–63 characters, no leading or trailing hyphen (§4).</remarks>
public sealed record UserId
{
    private UserId(string value) => Value = value;

    /// <summary>Gets the user label.</summary>
    public string Value { get; }

    /// <summary>Parses a user label.</summary>
    /// <param name="value">The label text.</param>
    /// <returns>The user id.</returns>
    /// <exception cref="FormatException">Thrown when <paramref name="value"/> is not a valid label.</exception>
    public static UserId Parse(string value) =>
        TryParse(value, out UserId? result)
            ? result
            : throw new FormatException($"A user id must be {Label.SyntaxDescription}.");

    /// <summary>Tries to parse a user label.</summary>
    /// <param name="value">The label text, or <see langword="null"/>.</param>
    /// <param name="result">The user id when the text is a valid label; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is a valid label.</returns>
    public static bool TryParse(string? value, [NotNullWhen(true)] out UserId? result)
    {
        result = value is not null && Label.IsValid(value) ? new UserId(value) : null;
        return result is not null;
    }

    /// <summary>Returns the label text.</summary>
    /// <returns><see cref="Value"/>.</returns>
    public override string ToString() => Value;
}
