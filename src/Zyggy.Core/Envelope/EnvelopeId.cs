using System.Diagnostics.CodeAnalysis;

namespace Zyggy.Core.Envelope;

/// <summary>
/// An envelope id and bus file name: a ULID of 26 uppercase Crockford base32 characters (founding spec §4 "Write rules",
/// file name = ULID + <c>.md</c>).
/// </summary>
/// <remarks>
/// The first character is <c>0</c>–<c>7</c> (a 128-bit value); the alphabet excludes <c>I</c>, <c>L</c>, <c>O</c> and
/// <c>U</c>. The ULID implementation is an internal detail.
/// </remarks>
public sealed record EnvelopeId
{
    private const int Length = 26;

    private readonly Ulid _ulid;

    private EnvelopeId(Ulid ulid, string value)
    {
        _ulid = ulid;
        Value = value;
    }

    /// <summary>Gets the 26-character id.</summary>
    public string Value { get; }

    /// <summary>Gets the millisecond timestamp encoded in the id, in UTC.</summary>
    public DateTimeOffset Timestamp => _ulid.Time;

    /// <summary>Creates a new id for <paramref name="instant"/>: its millisecond timestamp plus 80 random bits.</summary>
    /// <param name="instant">The creation instant; the caller owns the clock.</param>
    /// <returns>A new id.</returns>
    public static EnvelopeId New(DateTimeOffset instant)
    {
        var ulid = Ulid.NewUlid(instant);
        return new EnvelopeId(ulid, ulid.ToString());
    }

    /// <summary>Parses an id.</summary>
    /// <param name="value">The id text.</param>
    /// <returns>The id.</returns>
    /// <exception cref="FormatException">Thrown when <paramref name="value"/> is not a ULID.</exception>
    public static EnvelopeId Parse(string value) =>
        TryParse(value, out EnvelopeId? result)
            ? result
            : throw new FormatException("An envelope id must be 26 uppercase Crockford base32 characters starting with 0-7.");

    /// <summary>Tries to parse an id.</summary>
    /// <param name="value">The id text, or <see langword="null"/>.</param>
    /// <param name="result">The id when the text is a valid ULID; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is a valid ULID.</returns>
    public static bool TryParse(string? value, [NotNullWhen(true)] out EnvelopeId? result)
    {
        result = null;
        if (value is null || !IsCanonicalText(value) || !Ulid.TryParse(value, out Ulid ulid))
        {
            return false;
        }

        result = new EnvelopeId(ulid, value);
        return true;
    }

    /// <summary>Returns the 26-character id.</summary>
    /// <returns><see cref="Value"/>.</returns>
    public override string ToString() => Value;

    /// <summary>Determines whether two ids are equal (same text).</summary>
    /// <param name="other">The other id.</param>
    /// <returns><see langword="true"/> when both ids have the same text.</returns>
    public bool Equals(EnvelopeId? other) => other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <summary>Returns a hash code of the id text.</summary>
    /// <returns>The hash code.</returns>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    // The ULID decoder is lenient (lowercase, I/L/O/U); the protocol accepts only the canonical uppercase form.
    private static bool IsCanonicalText(string value)
    {
        if (value.Length != Length || value[0] is < '0' or > '7')
        {
            return false;
        }

        foreach (char c in value)
        {
            bool ok = c is (>= '0' and <= '9') or (>= 'A' and <= 'Z') && c is not ('I' or 'L' or 'O' or 'U');
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }
}
