using System.Text;
using System.Text.RegularExpressions;

namespace Zyggy.Core.Memory;

/// <summary>
/// The <c>facts.sh</c> rules for a candidate fact (spec 33 AC-12), in the script's order: empty, non-letter start, e-mail address,
/// URL, secret pattern, phone. The phone rule is the script's (a <c>+</c> with 8 digits or a leading <c>0</c> with 9), not the
/// dream's <see cref="ContactDetailPatterns"/>.
/// </summary>
internal static partial class FactValidator
{
    /// <summary>The longest fact kept, in characters; a longer one ends in "…".</summary>
    public const int MaxCharacters = 240;

    /// <summary>The refusal reasons in the order the counts line names them; secret patterns follow, in the order first met.</summary>
    public static readonly IReadOnlyList<string> Reasons = ["empty", "non-letter start", "e-mail address", "url", "phone"];

    /// <summary><c>tr '\t' ' ' | tr -d '\000-\010\013-\037\177'</c>, then <see cref="TextCollapse.Line"/>.</summary>
    public static string Clean(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var builder = new StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            if (c == '\t')
            {
                builder.Append(' ');
            }
            else if (c is not ('\0' or (>= '\u0001' and <= '\u0008') or (>= '\u000b' and <= '\u001f') or '\u007f'))
            {
                builder.Append(c);
            }
        }

        return TextCollapse.Line(builder.ToString());
    }

    /// <summary>Returns why <paramref name="fact"/> is refused, or <see langword="null"/> when it may be kept.</summary>
    public static string? Refusal(string fact, SecretPatterns patterns)
    {
        ArgumentNullException.ThrowIfNull(fact);
        ArgumentNullException.ThrowIfNull(patterns);
        if (fact.Length == 0)
        {
            return "empty";
        }

        if (!Rune.TryGetRuneAt(fact, 0, out var first) || !Rune.IsLetter(first))
        {
            return "non-letter start";
        }

        if (Email().IsMatch(fact))
        {
            return "e-mail address";
        }

        if (UrlIgnoreCase().IsMatch(fact))
        {
            return "url";
        }

        if (patterns.TryMatch(fact, out var name))
        {
            return "secret pattern " + name;
        }

        foreach (Match candidate in PhoneCandidate().Matches(fact))
        {
            var digits = candidate.Value.Count(char.IsAsciiDigit);
            if ((candidate.Value.Contains('+', StringComparison.Ordinal) && digits >= 8) || digits >= 9)
            {
                return "phone";
            }
        }

        return null;
    }

    /// <summary>Whether <paramref name="text"/> holds an e-mail address (case-sensitive, as the script's <c>grep -E</c>).</summary>
    public static bool HoldsEmail(string text) => Email().IsMatch(text);

    /// <summary>Whether <paramref name="text"/> holds a URL, case-sensitively (the script's <c>--source</c> check).</summary>
    public static bool HoldsUrl(string text) => Url().IsMatch(text);

    /// <summary>The first 239 characters (Unicode scalar values) and "…".</summary>
    public static string Cut(string fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        var builder = new StringBuilder();
        var count = 0;
        foreach (var rune in fact.EnumerateRunes())
        {
            if (count++ == MaxCharacters - 1)
            {
                break;
            }

            builder.Append(rune.ToString());
        }

        return builder.Append('…').ToString();
    }

    [GeneratedRegex("[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    [GeneratedRegex("([A-Za-z][A-Za-z0-9+.-]*://|www\\.)", RegexOptions.CultureInvariant)]
    private static partial Regex Url();

    [GeneratedRegex("([A-Za-z][A-Za-z0-9+.-]*://|www\\.)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex UrlIgnoreCase();

    // "+" or a leading 0, then digits with spaces, dots, slashes, hyphens or brackets.
    [GeneratedRegex("(\\+|(^|[^0-9A-Za-z])0)[0-9][0-9 ./()-]{6,}[0-9]", RegexOptions.CultureInvariant)]
    private static partial Regex PhoneCandidate();
}
