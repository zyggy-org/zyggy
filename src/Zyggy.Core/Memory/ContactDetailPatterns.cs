using System.Text.RegularExpressions;

namespace Zyggy.Core.Memory;

/// <summary>
/// E-mail addresses and phone numbers, which memory never stores (§8 data protection): an e-mail address, an international number
/// (<c>+</c> or <c>00</c>) or a national number starting with <c>0</c>. Dates, ULIDs, slugs and version numbers do not match.
/// </summary>
internal static partial class ContactDetailPatterns
{
    public static bool Contains(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Email().IsMatch(text) || International().IsMatch(text) || National().IsMatch(text);
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Email();

    [GeneratedRegex(@"(?<![\w-])(?:\+|00)[1-9][\d ()./-]{7,}\d", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex International();

    [GeneratedRegex(@"(?<![\w-])0\d{1,3}[ ./-]?\d{2,3}(?:[ .-]?\d{2}){2,3}(?![\w-])", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex National();
}
