using System.Diagnostics.CodeAnalysis;

namespace Zyggy.Core.Tenancy;

/// <summary>
/// Identifies a tenant (an organisation, <c>&lt;org&gt;</c>) — founding spec §14. Every envelope, bus path, secret name and
/// <c>key_id</c> carries one. There is deliberately no default tenant: the value always comes from configuration or from the
/// caller's principal (§13 "Tenancy shape from P0").
/// </summary>
/// <remarks>Syntax: a lowercase label <c>[a-z0-9-]</c>, 1–63 characters, no leading or trailing hyphen (§4).</remarks>
public sealed record TenantId
{
    private TenantId(string value) => Value = value;

    /// <summary>Gets the tenant label exactly as written on the bus.</summary>
    public string Value { get; }

    /// <summary>Parses a tenant label.</summary>
    /// <param name="value">The label text.</param>
    /// <returns>The tenant id.</returns>
    /// <exception cref="FormatException">Thrown when <paramref name="value"/> is not a valid label.</exception>
    public static TenantId Parse(string value) =>
        TryParse(value, out TenantId? result)
            ? result
            : throw new FormatException($"A tenant id must be {Label.SyntaxDescription}.");

    /// <summary>Tries to parse a tenant label.</summary>
    /// <param name="value">The label text, or <see langword="null"/>.</param>
    /// <param name="result">The tenant id when the text is a valid label; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is a valid label.</returns>
    public static bool TryParse(string? value, [NotNullWhen(true)] out TenantId? result)
    {
        result = value is not null && Label.IsValid(value) ? new TenantId(value) : null;
        return result is not null;
    }

    /// <summary>Returns the label text.</summary>
    /// <returns><see cref="Value"/>.</returns>
    public override string ToString() => Value;
}
