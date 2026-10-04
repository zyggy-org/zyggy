using System.Globalization;
using System.Text.RegularExpressions;

namespace Zyggy.Core.Memory;

/// <summary>
/// Byte caps of the digest sections, wrapper and marker included: 6,000 / 6,000 / 8,000 by default, never above 9,500 so a
/// section stays under Claude Code's 10,000-character hook-output limit (27 <c>zy_cap_bytes</c>).
/// </summary>
public sealed partial record DigestOptions
{
    /// <summary>The highest cap any section may have.</summary>
    public const int Ceiling = 9500;

    /// <summary>Gets the <c>identity</c> cap in bytes.</summary>
    public int IdentityBytes { get; init; } = Default(DigestSection.Identity);

    /// <summary>Gets the <c>index</c> cap in bytes.</summary>
    public int IndexBytes { get; init; } = Default(DigestSection.Index);

    /// <summary>Gets the <c>daily</c> cap in bytes.</summary>
    public int DailyBytes { get; init; } = Default(DigestSection.Daily);

    /// <summary>Returns the effective cap of a section: a non-positive value falls back to the default, a larger one is clamped.</summary>
    /// <param name="section">The section.</param>
    /// <returns>The cap in bytes.</returns>
    public int CapFor(DigestSection section)
    {
        var value = section switch
        {
            DigestSection.Identity => IdentityBytes,
            DigestSection.Index => IndexBytes,
            _ => DailyBytes,
        };
        return value < 1 ? Default(section) : Math.Min(value, Ceiling);
    }

    /// <summary>
    /// Parses a <c>ZYGGY_DIGEST_BYTES_&lt;SECTION&gt;</c> value as 27 does: anything but <c>^[1-9][0-9]{0,8}$</c> falls back to the
    /// section's default, a larger number is clamped to <see cref="Ceiling"/>.
    /// </summary>
    /// <param name="value">The variable's text, or <see langword="null"/>.</param>
    /// <param name="section">The section.</param>
    /// <returns>The cap in bytes.</returns>
    public static int ParseCap(string? value, DigestSection section) =>
        value is not null && CapSyntax().IsMatch(value)
            ? Math.Min(int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture), Ceiling)
            : Default(section);

    private static int Default(DigestSection section) => section == DigestSection.Daily ? 8000 : 6000;

    [GeneratedRegex("^[1-9][0-9]{0,8}$", RegexOptions.CultureInvariant)]
    private static partial Regex CapSyntax();
}
