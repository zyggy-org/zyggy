using System.Globalization;
using System.Text.RegularExpressions;

using Zyggy.Core.Memory;

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

    /// <summary>
    /// Removes the private contact details from <paramref name="text"/>. The text is normalised first, like the work mail filter's:
    /// format characters removed (<see cref="FormatCharacters"/>) and every decimal digit as its ASCII digit.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The text without them.</returns>
    public static string Strip(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string result = AsciiDigits(FormatCharacters.Remove(text));
        result = EmailAddress().Replace(result, Removed);
        result = SpacedEmailAddress().Replace(result, Removed);
        result = PostalAddress().Replace(result, Removed);
        return PhoneCandidate().Replace(result, m => IsPhoneNumber(m.Value) ? Removed : m.Value);
    }

    // At least nine digits (the Belgian national forms and E.164; a longer run, two numbers written one after the other, goes too), and
    // not a date that runs on into a number.
    private static bool IsPhoneNumber(string candidate) =>
        candidate.Count(char.IsAsciiDigit) >= 9 && !DatePrefix().IsMatch(candidate.TrimStart('('));

    // Every Unicode decimal digit (full-width, Arabic-Indic, \u2026) as its ASCII digit, one character for one, so a number in another
    // digit script is matched like an ASCII one.
    private static string AsciiDigits(string text) =>
        string.Create(text.Length, text, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                char c = source[i];
                int value = char.IsSurrogate(c) ? -1 : CharUnicodeInfo.GetDecimalDigitValue(c);
                span[i] = value is >= 0 and <= 9 ? (char)('0' + value) : c;
            }
        });

    [GeneratedRegex(@"[\p{L}\p{N}._%+\-]+@[\p{L}\p{N}\-]+(?:\.[\p{L}\p{N}\-]+)+", RegexOptions.CultureInvariant)]
    private static partial Regex EmailAddress();

    // An address written with spaces around the '@' ("jan.peeters @ example.test"); its last label starts with two letters, so a time
    // or an amount ("price @ 10.30") is not taken for one.
    [GeneratedRegex(@"[\p{L}\p{N}._%+\-]+\s*@\s*[\p{L}\p{N}\-]+(?:\.[\p{L}\p{N}\-]+)*\.\p{L}{2,}(?![\p{L}\p{N}])", RegexOptions.CultureInvariant)]
    private static partial Regex SpacedEmailAddress();

    // Starts with '+', '00' or '0' (an international or a national number), optionally in brackets ("(0475) 12 34 56"), then digits
    // with the separators people write (space, '.', '/', '-', the Unicode dashes, brackets), ending on a digit. Not inside a longer
    // number or reference ("2026-0012345", "PO 4500\u2026"), but right after a letter label with or without '.', ':', '/' or '-'
    // ("T.0475 \u2026", "GSM/0475\u2026", "Tel.:+32 \u2026", "t\u00e9l.02/\u2026", "GSM0475\u2026").
    [GeneratedRegex(
        @"(?:(?<![\p{L}\p{N}\-./+(])|(?<=(?<![\p{L}\p{N}])\p{L}{1,15}\.?\s*[.:/\-]?\s*))"
        + @"\(?(?:\+|0)[0-9\s./()\-\u2010-\u2015]{7,}[0-9](?![\p{L}\p{N}])",
        RegexOptions.CultureInvariant)]
    private static partial Regex PhoneCandidate();

    [GeneratedRegex(@"^[0-9]{1,2}[./\-][0-9]{1,2}[./\-][0-9]{2,4}", RegexOptions.CultureInvariant)]
    private static partial Regex DatePrefix();

    // Three street forms, each with an optional box and an optional ", <postcode> <Town>": a Dutch or English street word and its number
    // ("Kerkstraat 12", "Grote Steenweg 120 bus 3"), a French street ("rue de la Loi 16"), an English number-first street
    // ("12 Baker Street"). The number may follow a comma ("rue de la Loi, 16") and a number word ("nr. 12", "n° 12"); the box is
    // "bus", "box", "bte", "boîte" or '/'; the postcode may carry "B-".
    [GeneratedRegex(
        @"(?:"
        + @"(?<![\p{L}])[\p{L}\-]*(?:straat|laan|weg|plein|lei|dreef|baan|kaai|kade|singel|steeg|markt|vliet|dijk|gracht|plaats|park|hof|street|road|avenue|lane|drive),?\s+"
        + @"(?:(?:nr|no|n°|num[ée]ro)\.?\s*)?[0-9]{1,4}[a-z]?"
        + @"|(?<![\p{L}])(?:rue|avenue|av\.|chauss[ée]e|boulevard|bd|place|chemin|square|all[ée]e)\s+(?:[\p{L}'’\-]+,?\s+){0,5}"
        + @"(?:(?:nr|no|n°|num[ée]ro)\.?\s*)?[0-9]{1,4}[a-z]?"
        + @"|(?<![\p{L}\p{N}])[0-9]{1,4}[a-z]?\s+(?-i:(?:\p{Lu}[\p{L}\-]+\s+){1,3}(?:Street|St\.|Road|Rd\.|Avenue|Ave\.|Lane|Drive|Way))"
        + @")"
        + @"(?:\s*(?:bus|box|bte|bo[iî]te|/)\.?\s*[0-9]+[a-z]?)?"
        + @"(?:,?\s*(?:B-?\s?)?[1-9][0-9]{3}\s+(?-i:\p{Lu})[\p{L}\-]+)?"
        + @"(?![\p{L}\p{N}])",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex PostalAddress();
}
