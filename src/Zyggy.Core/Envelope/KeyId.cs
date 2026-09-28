using System.Diagnostics.CodeAnalysis;
using System.Globalization;

using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Envelope;

/// <summary>
/// Names the HMAC key an envelope is signed with: <c>&lt;tenant&gt;/&lt;n&gt;</c> (founding spec §4 "Signature", §14). Keys never
/// cross tenants; rotation adds <c>&lt;tenant&gt;/2</c> next to <c>&lt;tenant&gt;/1</c>.
/// </summary>
public sealed record KeyId
{
    /// <summary>Creates a key id.</summary>
    /// <param name="tenant">The tenant that owns the key.</param>
    /// <param name="number">The key number, 1 or more.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="tenant"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="number"/> is less than 1.</exception>
    public KeyId(TenantId tenant, int number)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);
        Tenant = tenant;
        Number = number;
    }

    /// <summary>Gets the tenant that owns the key.</summary>
    public TenantId Tenant { get; }

    /// <summary>Gets the key number.</summary>
    public int Number { get; }

    /// <summary>Gets the tenant-relative secret name of the key material: <c>hmac/&lt;n&gt;</c> (§8).</summary>
    public SecretName SecretName => SecretName.Parse("hmac/" + Number.ToString(CultureInfo.InvariantCulture));

    /// <summary>Parses a key id.</summary>
    /// <param name="value">The text <c>&lt;tenant&gt;/&lt;n&gt;</c>.</param>
    /// <returns>The key id.</returns>
    /// <exception cref="FormatException">Thrown when <paramref name="value"/> is not a valid key id.</exception>
    public static KeyId Parse(string value) =>
        TryParse(value, out KeyId? result)
            ? result
            : throw new FormatException("A key id must be <tenant>/<n> with n a positive integer without leading zeros.");

    /// <summary>Tries to parse a key id.</summary>
    /// <param name="value">The text <c>&lt;tenant&gt;/&lt;n&gt;</c>, or <see langword="null"/>.</param>
    /// <param name="result">The key id when the text is valid; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is a valid key id.</returns>
    public static bool TryParse(string? value, [NotNullWhen(true)] out KeyId? result)
    {
        result = null;
        int slash = value?.IndexOf('/', StringComparison.Ordinal) ?? -1;
        if (value is null || slash < 0)
        {
            return false;
        }

        ReadOnlySpan<char> digits = value.AsSpan(slash + 1);
        if (digits.IsEmpty || digits[0] is < '1' or > '9' || !IsAllDigits(digits)
            || !int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int number)
            || !TenantId.TryParse(value[..slash], out TenantId? tenant))
        {
            return false;
        }

        result = new KeyId(tenant, number);
        return true;
    }

    /// <summary>Returns the wire form <c>&lt;tenant&gt;/&lt;n&gt;</c>.</summary>
    /// <returns>The key id text.</returns>
    public override string ToString() => Tenant.Value + "/" + Number.ToString(CultureInfo.InvariantCulture);

    private static bool IsAllDigits(ReadOnlySpan<char> text)
    {
        foreach (char c in text)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }
}
