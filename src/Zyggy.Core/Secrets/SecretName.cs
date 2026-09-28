using System.Diagnostics.CodeAnalysis;

namespace Zyggy.Core.Secrets;

/// <summary>
/// The tenant-relative name of a secret, for example <c>hmac/1</c>. The store composes the full name
/// <c>zyggy/&lt;tenant&gt;/&lt;name&gt;</c> (founding spec §8), so a name can never reach another tenant's secrets.
/// </summary>
/// <remarks>
/// Syntax: one or more segments of <c>[a-z0-9-]</c> separated by single <c>/</c>, at most 64 characters; no leading,
/// trailing or doubled slash and no <c>.</c>, so no traversal is expressible.
/// </remarks>
public sealed record SecretName
{
    private const int MaxLength = 64;

    private SecretName(string value) => Value = value;

    /// <summary>Gets the relative secret name.</summary>
    public string Value { get; }

    /// <summary>Parses a secret name.</summary>
    /// <param name="value">The name text.</param>
    /// <returns>The secret name.</returns>
    /// <exception cref="FormatException">Thrown when <paramref name="value"/> is not a valid secret name.</exception>
    public static SecretName Parse(string value) =>
        TryParse(value, out SecretName? result)
            ? result
            : throw new FormatException(
                "A secret name must be segments of [a-z0-9-] separated by single '/', at most 64 characters.");

    /// <summary>Tries to parse a secret name.</summary>
    /// <param name="value">The name text, or <see langword="null"/>.</param>
    /// <param name="result">The secret name when the text is valid; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is a valid secret name.</returns>
    public static bool TryParse(string? value, [NotNullWhen(true)] out SecretName? result)
    {
        result = value is not null && IsValid(value) ? new SecretName(value) : null;
        return result is not null;
    }

    /// <summary>Returns the relative name.</summary>
    /// <returns><see cref="Value"/>.</returns>
    public override string ToString() => Value;

    private static bool IsValid(string value)
    {
        if (value.Length is 0 or > MaxLength || value[0] == '/' || value[^1] == '/')
        {
            return false;
        }

        char previous = '\0';
        foreach (char c in value)
        {
            bool ok = c == '/' ? previous != '/' : (c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');
            if (!ok)
            {
                return false;
            }

            previous = c;
        }

        return true;
    }
}
