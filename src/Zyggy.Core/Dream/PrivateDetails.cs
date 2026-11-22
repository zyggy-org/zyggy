using System.Text.RegularExpressions;

namespace Zyggy.Core.Dream;

/// <summary>
/// Spec 58 D-Q18: before the dream keeps a work mail line in the business archive, the private contact details of third parties are
/// removed — e-mail addresses, phone numbers (Belgian and international forms: <c>+32 475 12 34 56</c>, <c>0032 …</c>,
/// <c>02/123.45.67</c>, <c>0475123456</c>; nine digits or more) and postal addresses (a street with its house number and box, with
/// the postcode and town that follow) — each replaced by <see cref="Removed"/>. Name, role and organisation stay. Best-effort, like
/// the laptop's filter: unusual wording can pass; dates, amounts and reference numbers are left alone.
/// </summary>
internal static partial class PrivateDetails
{
    /// <summary>What stands in place of a removed detail.</summary>
    public const string Removed = "[private detail removed]";

    /// <summary>Removes the private contact details from <paramref name="text"/>.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The text without them.</returns>
    public static string Strip(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string result = EmailAddress().Replace(text, Removed);
        result = PostalAddress().Replace(result, Removed);
        return PhoneCandidate().Replace(result, m => IsPhoneNumber(m.Value) ? Removed : m.Value);
    }

    // At least nine digits (the Belgian national forms and E.164; a longer run, two numbers written one after the other, goes too), and
    // not a date that runs on into a number.
    private static bool IsPhoneNumber(string candidate) =>
        candidate.Count(char.IsAsciiDigit) >= 9 && !DatePrefix().IsMatch(candidate);

    [GeneratedRegex(@"[\p{L}\p{N}._%+\-]+@[\p{L}\p{N}\-]+(?:\.[\p{L}\p{N}\-]+)+", RegexOptions.CultureInvariant)]
    private static partial Regex EmailAddress();

    // Starts with '+', '00' or '0' (an international or a national number), not inside a word or a longer number or reference, then
    // digits with the separators people write (space, '.', '/', '-', the Unicode dashes, brackets), ending on a digit.
    [GeneratedRegex(@"(?<![\p{L}\p{N}\-./+])(?:\+|0)[0-9\s./()\-\u2010-\u2015]{7,}[0-9](?![\p{L}\p{N}])", RegexOptions.CultureInvariant)]
    private static partial Regex PhoneCandidate();

    [GeneratedRegex(@"^[0-9]{1,2}[./\-][0-9]{1,2}[./\-][0-9]{2,4}", RegexOptions.CultureInvariant)]
    private static partial Regex DatePrefix();

    // Three street forms, each with an optional box and an optional ", <postcode> <Town>": a Dutch or English street word and its number
    // ("Kerkstraat 12", "Grote Steenweg 120 bus 3"), a French street ("rue de la Loi 16"), an English number-first street
    // ("12 Baker Street").
    [GeneratedRegex(
        @"(?:"
        + @"(?<![\p{L}])[\p{L}\-]*(?:straat|laan|weg|plein|lei|dreef|baan|kaai|singel|steeg|markt|street|road|avenue|lane|drive)\s+[0-9]{1,4}[a-z]?"
        + @"|(?<![\p{L}])(?:rue|avenue|av\.|chauss[ée]e|boulevard|bd|place|chemin|square|all[ée]e)\s+(?:[\p{L}'’\-]+\s+){0,5}[0-9]{1,4}[a-z]?"
        + @"|(?<![\p{L}\p{N}])[0-9]{1,4}[a-z]?\s+(?-i:(?:\p{Lu}[\p{L}\-]+\s+){1,3}(?:Street|St\.|Road|Rd\.|Avenue|Ave\.|Lane|Drive|Way))"
        + @")"
        + @"(?:\s*(?:bus|box|/)\s*[0-9]+[a-z]?)?"
        + @"(?:,?\s*[1-9][0-9]{3}\s+(?-i:\p{Lu})[\p{L}\-]+)?"
        + @"(?![\p{L}\p{N}])",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex PostalAddress();
}
