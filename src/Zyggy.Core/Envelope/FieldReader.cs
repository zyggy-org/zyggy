using System.Globalization;

namespace Zyggy.Core.Envelope;

/// <summary>
/// Typed interpretation of front-matter scalar text (founding spec §4 "Mandatory fields per type"): absent and null are
/// the same, a node of the wrong kind is <see cref="EnvelopeRejectionReason.InvalidField"/>. Every rejection names the key
/// and never the value.
/// </summary>
internal static class FieldReader
{
    private static readonly string[] TimestampFormats = ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"];

    internal static bool IsNull(string text) => text is "" or "null" or "Null" or "NULL" or "~";

    /// <summary>Reads the text of <paramref name="key"/>; <see langword="null"/> when absent or null.</summary>
    internal static EnvelopeRejection? Text(FrontMatterMapping map, string key, bool mandatory, out string? text)
    {
        text = null;
        if (!map.Entries.TryGetValue(key, out FrontMatterNode? node) || (node is FrontMatterScalar s && IsNull(s.Value)))
        {
            return mandatory ? Missing(key) : null;
        }

        if (node is not FrontMatterScalar scalar)
        {
            return Invalid(key, "must be a scalar");
        }

        text = scalar.Value;
        return null;
    }

    /// <summary>Reads a list of non-empty strings; <see langword="null"/> when absent or null.</summary>
    internal static EnvelopeRejection? StringList(FrontMatterMapping map, string key, out IReadOnlyList<string>? list)
    {
        list = null;
        if (!map.Entries.TryGetValue(key, out FrontMatterNode? node) || (node is FrontMatterScalar s && IsNull(s.Value)))
        {
            return null;
        }

        if (node is not FrontMatterSequence sequence)
        {
            return Invalid(key, "must be a sequence of non-empty strings");
        }

        var items = new List<string>(sequence.Items.Count);
        foreach (FrontMatterNode item in sequence.Items)
        {
            if (item is not FrontMatterScalar scalar || IsNull(scalar.Value))
            {
                return Invalid(key, "must be a sequence of non-empty strings");
            }

            items.Add(scalar.Value);
        }

        list = items;
        return null;
    }

    internal static bool TryInt(string text, out int value) =>
        int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);

    internal static bool TryDecimal(string text, out decimal value) =>
        decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);

    internal static bool TryBool(string text, out bool value)
    {
        (bool ok, value) = text switch
        {
            "true" or "True" or "TRUE" => (true, true),
            "false" or "False" or "FALSE" => (true, false),
            _ => (false, false),
        };
        return ok;
    }

    /// <summary>Parses an RFC 3339 date-time (<c>T</c>, seconds, optional fraction, <c>Z</c> or <c>±hh:mm</c>) into UTC.</summary>
    internal static bool TryTimestamp(string text, out DateTimeOffset value)
    {
        value = default;
        bool hasZone = text.EndsWith('Z')
            || (text.Length > 6 && text[^6] is '+' or '-' && text[^3] == ':');
        return hasZone
            && DateTimeOffset.TryParseExact(text, TimestampFormats, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out value);
    }

    internal static EnvelopeRejection Missing(string key) =>
        new(EnvelopeRejectionReason.MissingField, key, "mandatory field is missing or null");

    internal static EnvelopeRejection Invalid(string key, string rule) =>
        new(EnvelopeRejectionReason.InvalidField, key, "field " + rule);
}
