using System.Globalization;
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
/// check digit, or a whole-word term of the shared sensitive list (<c>sensitive-terms.txt</c>, D-Q9), is kept on the laptop; so is a
/// mail the session marked in doubt, a protected mail (its body was never read, so it cannot be judged), a mail without facts, and a
/// mail whose facts name one of its attachments (attachments never cross). Best-effort, as the owner accepted (2026-11-21): unusual
/// wording can pass. It never returns the matched value.
/// </summary>
internal sealed partial class WorkMailFilter
{
    private readonly Regex? _terms;

    private WorkMailFilter(IReadOnlyList<string> terms)
    {
        Terms = terms;
        _terms = terms.Count == 0
            ? null
            : new Regex(
                @"(?<![\p{L}\p{N}])(?:" + string.Join('|', terms.Select(Regex.Escape)) + @")(?![\p{L}\p{N}])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
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
    /// Whether <paramref name="text"/> holds a Belgian national register number: 11 digits (optionally grouped as
    /// <c>YY.MM.DD-NNN.CC</c> with dots, dashes or spaces) whose check digits are <c>97 − (first nine mod 97)</c>, or the same over
    /// <c>2</c> and the first nine for a birth from 2000.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>Whether one was found.</returns>
    public static bool HasNationalRegisterNumber(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (Match match in NationalRegisterNumber().Matches(text))
        {
            string digits = string.Concat(match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value, match.Groups[4].Value);
            long first = long.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
            int check = int.Parse(match.Groups[5].Value, NumberStyles.None, CultureInfo.InvariantCulture);
            if (97 - (first % 97) == check || 97 - ((2_000_000_000L + first) % 97) == check)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether <paramref name="text"/> holds a term of the list as a whole word, ignoring case.</summary>
    /// <param name="text">The text.</param>
    /// <returns>Whether one was found.</returns>
    public bool HasTerm(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return _terms is not null && _terms.IsMatch(text);
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
        if (texts.Any(t => HasNationalRegisterNumber(t) || HasTerm(t)))
        {
            return WorkMailVerdict.KeepSensitive;
        }

        bool namesAttachment = facts.Any(f => mail.AttachmentNames.Any(a => a.Length > 0 && f.Contains(a, StringComparison.OrdinalIgnoreCase)));
        return doubt || mail.Body is null || facts.Count == 0 || namesAttachment ? WorkMailVerdict.KeepInDoubt : WorkMailVerdict.Share;
    }

    // Eleven digits, optionally grouped YY.MM.DD-NNN.CC with one dot, dash or space between groups, not inside a longer number.
    [GeneratedRegex(@"(?<!\d)(\d{2})[.\- ]?(\d{2})[.\- ]?(\d{2})[.\- ]?(\d{3})[.\- ]?(\d{2})(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex NationalRegisterNumber();
}
