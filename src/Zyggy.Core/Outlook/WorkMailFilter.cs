using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Zyggy.Core.Outlook;

/// <summary>What the sensitive-data filter decided for one mail (spec 58 slice 4).</summary>
internal enum WorkMailVerdict
{
    /// <summary>Its facts may cross to Central.</summary>
    Share,

    /// <summary>It holds a national register number or a term of the shared sensitive list: nothing crosses, it is counted.</summary>
    KeepSensitive,

    /// <summary>The session marked it in doubt, or the filter could not judge it: nothing crosses, it is counted.</summary>
    KeepInDoubt,
}

/// <summary>
/// The sensitive-data filter of the work laptop (spec 58 slice 4, AC-9; the client's never-list): before anything of a mail reaches
/// the bus, a mail whose subject, sender, whole body or any fact the session wrote holds a national register number with a valid
/// check digit (in any digit script and with any common separators), or a term of the shared sensitive list (<c>sensitive-terms.txt</c>,
/// D-Q9; accents, case and whitespace normalised, words matched as prefixes for plurals and inflections), is kept on the laptop; so is a
/// mail the session marked in doubt, a protected mail (its body was never read, so it cannot be judged), a mail without facts, and a
/// mail whose facts name one of its attachments (attachments never cross; by its whole name or by its stem without the extension,
/// names normalised like terms with '-', '_', '.' and spaces alike; a stem under four letters or digits is not matched alone). Best-effort, as the owner accepted (2026-11-21): unusual
/// wording can pass. It never returns the matched value.
/// </summary>
internal sealed partial class WorkMailFilter
{
    /// <summary>The shortest stem (letters and digits, separators not counted) a fact must name for it to count as naming the attachment.</summary>
    internal const int MinStemLength = 4;

    private readonly Regex? _terms;

    private WorkMailFilter(IReadOnlyList<string> terms)
    {
        Terms = terms;

        // Each word of a normalised term starts a word of the normalised text and may run on (plurals, inflections: "patient" also
        // matches "patients", "patiënten"); the words of a term follow each other with one space, as the text is collapsed.
        var patterns = terms
            .Select(t => Normalise(t).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(words => words.Length > 0)
            .Select(words => string.Join(' ', words.Select(w => Regex.Escape(w) + @"[\p{L}\p{N}]*")))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        _terms = patterns.Count == 0
            ? null
            : new Regex(
                @"(?<![\p{L}\p{N}])(?:" + string.Join('|', patterns) + @")(?![\p{L}\p{N}])",
                RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1));
    }

    /// <summary>Gets the terms of the list, in file order.</summary>
    public IReadOnlyList<string> Terms { get; }

    /// <summary>Reads the term list: one term per line, trimmed; blank lines and lines starting with <c>#</c> are ignored.</summary>
    /// <param name="text">The file's text.</param>
    /// <returns>The filter.</returns>
    public static WorkMailFilter Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var terms = text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToList();
        return new WorkMailFilter(terms);
    }

    /// <summary>
    /// Whether <paramref name="text"/> holds a Belgian national register number: 11 digits, grouped <c>YY MM DD NNN CC</c> with up to
    /// three separators (whitespace, NBSP, <c>.</c>, <c>-</c>, <c>/</c>) between groups, whose check digits are
    /// <c>97 − (first nine mod 97)</c>, or the same over <c>2</c> and the first nine for a birth from 2000. Non-ASCII digits
    /// (full-width, Arabic-Indic, …) are read as their value first (NFKC, then every Unicode decimal digit), so only ASCII
    /// <c>[0-9]</c> is matched. Every start position is tried, so an earlier near-miss cannot hide a number. Never throws on any
    /// text and never returns or prints a digit of a candidate.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>Whether one was found.</returns>
    public static bool HasNationalRegisterNumber(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (Match match in NationalRegisterNumber().Matches(AsciiDigits(text)))
        {
            long first = 0;
            for (var g = 1; g <= 4; g++)
            {
                foreach (char c in match.Groups[g].ValueSpan)
                {
                    first = (first * 10) + (c - '0');
                }
            }

            string checkText = match.Groups[5].Value;
            int check = ((checkText[0] - '0') * 10) + (checkText[1] - '0');
            if (97 - (first % 97) == check || 97 - ((2_000_000_000L + first) % 97) == check)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="text"/> holds a term of the list. Terms and text are both normalised (NFKD, combining marks dropped,
    /// case-folded, every run of whitespace — line breaks, tabs, NBSP included — one space), and each word of a term matches the start
    /// of a word of the text, so plurals and inflections match too ("patient" → "patients", "patiënten").
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>Whether one was found.</returns>
    public bool HasTerm(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return _terms is not null && _terms.IsMatch(Normalise(text));
    }

    /// <summary>
    /// The form terms and text are compared in: NFKD, combining marks dropped, lower-case invariant, every whitespace run one space,
    /// trimmed.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The normalised text.</returns>
    internal static string Normalise(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string decomposed = text.Normalize(NormalizationForm.FormKD);
        var result = new StringBuilder(decomposed.Length);
        bool space = false;
        foreach (char c in decomposed)
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
            {
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                space = result.Length > 0;
                continue;
            }

            if (space)
            {
                result.Append(' ');
                space = false;
            }

            result.Append(Fold(c));
        }

        return result.ToString();
    }

    // The NFKD base of a precomposed Latin letter (U+00C0–U+017F) or a full-width ASCII form, lower-cased: the repository runs with
    // InvariantGlobalization, where string.Normalize does not decompose, so the decomposition the filter needs is spelled out here.
    // '_' = unchanged, '*' = two letters.
    private static string Fold(char c)
    {
        // Typographic apostrophes and quotes as their ASCII form, and the Latin ligatures of the Alphabetic Presentation Forms block
        // as their letters (œ, æ and ĳ are in the Latin table below).
        switch (c)
        {
            case '‘' or '’' or '‚' or '‛' or 'ʼ' or '′' or '´' or '`':
                return "'";
            case '“' or '”' or '„' or '‟' or '″' or '«' or '»':
                return "\"";
            case 'ﬀ':
                return "ff";
            case 'ﬁ':
                return "fi";
            case 'ﬂ':
                return "fl";
            case 'ﬃ':
                return "ffi";
            case 'ﬄ':
                return "ffl";
            case 'ﬅ' or 'ﬆ':
                return "st";
            default:
                break;
        }

        const string Latin =
            "aaaaaa*ceeeeiiii" + "dnooooo_ouuuuy_*" + "aaaaaa*ceeeeiiii" + "dnooooo_ouuuuy_y"
            + "aaaaaaccccccccdd" + "ddeeeeeeeeeegggg" + "gggghhhhiiiiiiii" + "ii**jjkkklllllll"
            + "lllnnnnnnnnnoooo" + "oo**rrrrrrssssss" + "ssttttttuuuuuuuu" + "uuuuwwyyyzzzzzzs";
        if (c is >= '！' and <= '～')
        {
            c = (char)(c - 0xFEE0);
        }

        if (c is < 'À' or > 'ſ' || Latin[c - 0xC0] == '_')
        {
            return char.ToLowerInvariant(c).ToString();
        }

        return Latin[c - 0xC0] != '*'
            ? Latin[c - 0xC0].ToString()
            : c switch
            {
                'Æ' or 'æ' => "ae",
                'ß' => "ss",
                'Ĳ' or 'ĳ' => "ij",
                _ => "oe",
            };
    }

    // NFKC, then every Unicode decimal digit (full-width, Arabic-Indic, Devanagari, …) as its ASCII digit, every full-width ASCII
    // form (．－／) as its ASCII character and every Unicode dash (hyphen, non-breaking hyphen, figure dash, en-dash, em-dash,
    // horizontal bar, minus) as '-' — spelled out because string.Normalize does not map them under InvariantGlobalization. Zero-width
    // characters (U+200B–U+200D, U+FEFF) are dropped, so they neither separate nor break a number.
    private static string AsciiDigits(string text)
    {
        string composed = text.Normalize(NormalizationForm.FormKC);
        var result = new StringBuilder(composed.Length);
        foreach (char c in composed)
        {
            if (c is >= '​' and <= '‍' or '﻿')
            {
                continue;
            }

            int value = CharUnicodeInfo.GetDecimalDigitValue(c);
            result.Append(value is >= 0 and <= 9 && !char.IsAsciiDigit(c) ? (char)('0' + value)
                : c is >= '！' and <= '～' ? (char)(c - 0xFEE0)
                : c is >= '‐' and <= '―' or '−' or '﹘' or '﹣' ? '-'
                : c);
        }

        return result.ToString();
    }

    /// <summary>Judges one mail and the facts the session wrote about it.</summary>
    /// <param name="mail">The mail, read with its whole body.</param>
    /// <param name="facts">The facts the session wrote.</param>
    /// <param name="doubt">Whether the session marked the mail in doubt.</param>
    /// <returns>The verdict.</returns>
    public WorkMailVerdict Check(OutlookMail mail, IReadOnlyList<string> facts, bool doubt)
    {
        ArgumentNullException.ThrowIfNull(mail);
        ArgumentNullException.ThrowIfNull(facts);
        string[] texts = [mail.Summary.Subject, mail.Summary.SenderName, mail.Body ?? string.Empty, .. facts];
        try
        {
            if (texts.Any(t => HasNationalRegisterNumber(t) || HasTerm(t)))
            {
                return WorkMailVerdict.KeepSensitive;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // A text the term match could not judge in time is in doubt; the exception (which may carry the text) goes nowhere.
            return WorkMailVerdict.KeepInDoubt;
        }

        bool namesAttachment;
        try
        {
            string[] factKeys = [.. facts.Select(NameKey)];
            namesAttachment = mail.AttachmentNames.SelectMany(AttachmentKeys).Any(key => factKeys.Any(f => ContainsWords(f, key)));
        }
        catch (RegexMatchTimeoutException)
        {
            return WorkMailVerdict.KeepInDoubt;
        }

        return doubt || mail.Body is null || facts.Count == 0 || namesAttachment ? WorkMailVerdict.KeepInDoubt : WorkMailVerdict.Share;
    }

    // The forms of an attachment name a fact may use: the whole name, and its stem without the extension when that stem holds at least
    // MinStemLength letters or digits (a shorter stem, like "cv" or "q4", would drop ordinary mail).
    private static IEnumerable<string> AttachmentKeys(string name)
    {
        string whole = NameKey(name);
        if (whole.Length > 0)
        {
            yield return whole;
        }

        int dot = name.LastIndexOf('.');
        string stem = dot > 0 ? NameKey(name[..dot]) : whole;
        if (stem != whole && stem.Count(char.IsLetterOrDigit) >= MinStemLength)
        {
            yield return stem;
        }
    }

    // A name or a fact normalised like a term, then every run of characters other than letters and digits (space, '-', '_', '.', …)
    // one space, so "Canary_Offer.PDF", "canary-offer.pdf" and "canary offer pdf" read alike.
    private static string NameKey(string text) => NameSeparators().Replace(Normalise(text), " ").Trim();

    // The words of key follow each other in text, starting and ending on a word boundary.
    private static bool ContainsWords(string text, string key) =>
        Regex.IsMatch(text, @"(?<![\p{L}\p{N}])" + Regex.Escape(key) + @"(?![\p{L}\p{N}])", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    [GeneratedRegex(@"[^\p{L}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex NameSeparators();

    // Eleven ASCII digits grouped 2-2-2-3-2 with up to three separators (whitespace, NBSP, . - /) between groups, not inside a longer
    // number. A zero-width lookahead captures, so every start position is tried (matches may overlap).
    [GeneratedRegex(@"(?<![0-9])(?=([0-9]{2})[\s .\-/]{0,3}([0-9]{2})[\s .\-/]{0,3}([0-9]{2})[\s .\-/]{0,3}([0-9]{3})[\s .\-/]{0,3}([0-9]{2})(?![0-9]))", RegexOptions.CultureInvariant)]
    private static partial Regex NationalRegisterNumber();
}
