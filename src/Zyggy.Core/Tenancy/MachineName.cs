using System.Diagnostics.CodeAnalysis;

namespace Zyggy.Core.Tenancy;

/// <summary>
/// Names a machine of a tenant: the envelope's <c>from</c> and <c>to</c> fields and the <c>nodes/&lt;machine&gt;/</c> bus folder
/// (founding spec §4).
/// </summary>
/// <remarks>Syntax: a lowercase label <c>[a-z0-9-]</c>, 1–63 characters, no leading or trailing hyphen (§4).</remarks>
public sealed record MachineName
{
    private MachineName(string value) => Value = value;

    /// <summary>Gets the machine label.</summary>
    public string Value { get; }

    /// <summary>Parses a machine label.</summary>
    /// <param name="value">The label text.</param>
    /// <returns>The machine name.</returns>
    /// <exception cref="FormatException">Thrown when <paramref name="value"/> is not a valid label.</exception>
    public static MachineName Parse(string value) =>
        TryParse(value, out MachineName? result)
            ? result
            : throw new FormatException($"A machine name must be {Label.SyntaxDescription}.");

    /// <summary>Tries to parse a machine label.</summary>
    /// <param name="value">The label text, or <see langword="null"/>.</param>
    /// <param name="result">The machine name when the text is a valid label; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is a valid label.</returns>
    public static bool TryParse(string? value, [NotNullWhen(true)] out MachineName? result)
    {
        result = value is not null && Label.IsValid(value) ? new MachineName(value) : null;
        return result is not null;
    }

    /// <summary>Returns the label text.</summary>
    /// <returns><see cref="Value"/>.</returns>
    public override string ToString() => Value;
}
