using System.Globalization;

namespace Zyggy.Core.Outlook;

/// <summary>Culture-independent dates for the outlook tools (spec 58 Constraints "Dates", AC-3): ISO 8601, invariant culture.</summary>
internal static class OutlookDates
{
    /// <summary>The format every written date uses.</summary>
    public const string Format = "yyyy-MM-ddTHH:mm:sszzz";

    private static readonly string[] SinceWithOffset = ["yyyy-MM-ddTHH:mm:sszzz", "yyyy-MM-ddTHH:mm:ssZ", "yyyy-MM-ddTHH:mmzzz", "yyyy-MM-ddTHH:mmZ"];

    /// <summary>Converts a local COM time of the Outlook session to an instant with its offset in <paramref name="zone"/>.</summary>
    /// <param name="local">The local time (unspecified kind).</param>
    /// <param name="zone">The session's time zone.</param>
    /// <returns>The instant.</returns>
    public static DateTimeOffset FromLocal(DateTime local, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified));
    }

    /// <summary>Writes a local COM time of the Outlook session as ISO 8601 with its offset in <paramref name="zone"/>.</summary>
    /// <param name="local">The local time (unspecified kind).</param>
    /// <param name="zone">The session's time zone.</param>
    /// <returns>The ISO 8601 text.</returns>
    public static string Write(DateTime local, TimeZoneInfo zone) => FromLocal(local, zone).ToString(Format, CultureInfo.InvariantCulture);

    /// <summary>Parses a since-date given to a tool: <c>yyyy-MM-dd</c> (midnight in <paramref name="zone"/>) or ISO 8601 with an offset.</summary>
    /// <param name="text">The text.</param>
    /// <param name="zone">The session's time zone.</param>
    /// <param name="since">The parsed instant.</param>
    /// <returns>Whether the text is a valid date.</returns>
    public static bool TryParseSince(string text, TimeZoneInfo zone, out DateTimeOffset since)
    {
        ArgumentNullException.ThrowIfNull(zone);
        since = default;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            since = FromLocal(day.ToDateTime(TimeOnly.MinValue), zone);
            return true;
        }

        return DateTimeOffset.TryParseExact(text, SinceWithOffset, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out since);
    }
}
