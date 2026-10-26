using System.Security.Cryptography;

namespace Zyggy.Core.Secrets;

/// <summary>
/// The fingerprint an owner compares across machines after entering a key (spec 09 AC-1, <c>rotate-hmac.md</c>): the first
/// eight lowercase hexadecimal characters of the value's SHA-256. Printable anywhere; it reveals nothing usable of the value.
/// </summary>
public static class SecretFingerprint
{
    /// <summary>The length of a fingerprint in characters.</summary>
    public const int Length = 8;

    /// <summary>Computes the fingerprint of <paramref name="value"/>.</summary>
    /// <param name="value">The secret bytes; must not be empty.</param>
    /// <returns>Eight lowercase hexadecimal characters.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="value"/> is empty.</exception>
    public static string Of(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
        {
            throw new ArgumentException("A secret value must not be empty.", nameof(value));
        }

        return Convert.ToHexStringLower(SHA256.HashData(value))[..Length];
    }
}
