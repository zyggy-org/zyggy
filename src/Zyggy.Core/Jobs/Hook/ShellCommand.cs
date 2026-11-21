using System.Globalization;
using System.Text;

namespace Zyggy.Core.Jobs.Hook;

/// <summary>The shell a command is written for: the <c>Bash</c> tool's or the <c>PowerShell</c> tool's.</summary>
internal enum ShellDialect
{
    /// <summary>A backslash is an escape there, so it is not part of a simple command; <c>@</c> is an ordinary character.</summary>
    Bash,

    /// <summary>A backslash is an ordinary path character there; <c>@</c> starts splatting and here-strings, so it is not simple.</summary>
    PowerShell,
}

/// <summary>
/// The shell text of a <c>Bash</c> or <c>PowerShell</c> tool call, read the way the job hook needs it (spec 15). The hook does not
/// try to understand a shell: a pattern rule vouches only for a <em>simple</em> command — plain words and plain quoted strings, made
/// of characters with one meaning in both shells — and everything else (expansion, substitution, redirection, grouping, comments,
/// escapes, wildcards, characters outside printable ASCII) is "not simple" and matches only a bare rule or <c>(*)</c>. The simple
/// language has no escapes, so three states (unquoted, single-quoted, double-quoted) read it exactly. Two exceptions (spec 59
/// slice 8, AC-11): a backslash inside double quotes in Bash is a path character unless bash reads it as an escape, and the four
/// harmless redirects of <see cref="HarmlessRedirects"/> are simple words — each in the shell it belongs to.
/// </summary>
internal static class ShellCommand
{
    /// <summary>The redirects a job may write without a file being written: in Bash <c>2>&amp;1</c>, <c>2>/dev/null</c>, <c>>/dev/null</c>; in PowerShell <c>2>&amp;1</c>, <c>2>$null</c>.</summary>
    public static readonly IReadOnlyList<string> HarmlessRedirects = ["2>&1", "2>/dev/null", ">/dev/null", "2>$null"];

    private const string UnquotedPunctuation = " \t-_./:=,+%;|&\r\n";

    private static readonly string[] BashRedirects = ["2>&1", "2>/dev/null", ">/dev/null"];
    private static readonly string[] PowerShellRedirects = ["2>&1", "2>$null"];

    /// <summary>The dialect of <paramref name="tool"/>'s shell.</summary>
    public static ShellDialect DialectOf(string tool) => tool == "PowerShell" ? ShellDialect.PowerShell : ShellDialect.Bash;

    /// <summary>
    /// Why <paramref name="command"/> is not a simple command, or <see langword="null"/> when it is one. Unquoted: ASCII letters and
    /// digits, space, tab, <c>- _ . / : = , + %</c>, the separators <c>; | &amp;</c> and line breaks, <c>@</c> in Bash, <c>\</c> in
    /// PowerShell, and a harmless redirect of the dialect as a word of its own. In single quotes: printable ASCII. In double quotes:
    /// printable ASCII without <c>$</c> and the backtick; in Bash a backslash there is a path character, a pair is one backslash, and
    /// one before a quote or a line break is an escape (refused). No line break inside a quote, no quote left open, no
    /// <c>name=value</c> as the first word of a part.
    /// </summary>
    public static string? WhyNotSimple(string command, ShellDialect dialect)
    {
        var quote = '\0';
        var inFirstWord = true;
        var firstWordStarted = false;
        var redirects = dialect == ShellDialect.PowerShell ? PowerShellRedirects : BashRedirects;
        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                else if (c is '\r' or '\n')
                {
                    return "a line break inside a quote";
                }
                else if (quote == '"' && c == '\\' && dialect == ShellDialect.Bash)
                {
                    // Spec 59 slice 8: "C:\src\a.txt" is a path; "a\"; rm x" hides a quote, "a\\" is one backslash before the closing quote.
                    if (i + 1 == command.Length)
                    {
                        return "a quote never closes";
                    }

                    if (command[i + 1] is '"' or '\r' or '\n')
                    {
                        return "an escape inside a quote";
                    }

                    if (command[i + 1] == '\\')
                    {
                        i++;
                    }
                }
                else if (!IsPrintableAscii(c) || (quote == '"' && c is '$' or '`'))
                {
                    return Describe(c);
                }

                continue;
            }

            if (c is '\'' or '"')
            {
                quote = c;
                firstWordStarted = true;
                continue;
            }

            if (RedirectAt(command, i, redirects) is > 0 and var redirect)
            {
                i += redirect - 1;
                firstWordStarted = true;
                continue;
            }

            var allowed = char.IsAsciiLetterOrDigit(c)
                || UnquotedPunctuation.Contains(c, StringComparison.Ordinal)
                || (c == '@' && dialect == ShellDialect.Bash)
                || (c == '\\' && dialect == ShellDialect.PowerShell);
            if (!allowed)
            {
                return Describe(c);
            }

            if (IsSeparator(c))
            {
                inFirstWord = true;
                firstWordStarted = false;
            }
            else if (c is ' ' or '\t')
            {
                inFirstWord = inFirstWord && !firstWordStarted;
            }
            else if (inFirstWord && c == '=')
            {
                // "X=1 rm x" runs rm with X set; a rule's pattern must describe the program, not an assignment in front of it.
                return "an assignment before the command";
            }
            else
            {
                firstWordStarted = true;
            }
        }

        return quote == '\0' ? null : "a quote never closes";
    }

    /// <summary>
    /// The trimmed, non-empty parts of a simple <paramref name="command"/>: split on <c>;</c>, <c>|</c>, <c>&amp;</c> and line breaks
    /// outside quotes; a harmless redirect stays whole in its part.
    /// </summary>
    public static IReadOnlyList<string> Parts(string command)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var quote = '\0';
        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (quote != '\0')
            {
                current.Append(c);
                quote = c == quote ? '\0' : quote;
            }
            else if (RedirectAt(command, i, HarmlessRedirects) is > 0 and var redirect)
            {
                current.Append(command, i, redirect);
                i += redirect - 1;
            }
            else if (IsSeparator(c))
            {
                Flush(parts, current);
            }
            else
            {
                current.Append(c);
                quote = c is '\'' or '"' ? c : '\0';
            }
        }

        Flush(parts, current);
        return parts;
    }

    /// <summary>
    /// The words of one part of a simple command, quotes removed: split on blanks outside quotes. <c>Blank</c> says whether the word
    /// held a blank inside its quotes — a sentence, not a path (deliverable 18, the read root).
    /// </summary>
    public static IReadOnlyList<(string Text, bool Blank)> Words(string part)
    {
        var words = new List<(string, bool)>();
        var current = new StringBuilder();
        var quote = '\0';
        var started = false;
        var blank = false;
        foreach (var c in part)
        {
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                else
                {
                    blank |= c is ' ' or '\t';
                    current.Append(c);
                }
            }
            else if (c is '\'' or '"')
            {
                quote = c;
                started = true;
            }
            else if (c is ' ' or '\t')
            {
                if (started)
                {
                    words.Add((current.ToString(), blank));
                }

                current.Clear();
                started = false;
                blank = false;
            }
            else
            {
                current.Append(c);
                started = true;
            }
        }

        if (started)
        {
            words.Add((current.ToString(), blank));
        }

        return words;
    }

    /// <summary>
    /// Whether <paramref name="text"/> names <paramref name="program"/> as a whole word (not next to a letter, a digit or <c>_</c>),
    /// case-insensitively, once the characters a shell drops are dropped: quotes, backticks, carets and line continuations — and
    /// again with every backslash and line break dropped, so <c>cu""rl</c>, <c>c'u'rl</c>, <c>cu\rl</c>, <c>cur`l</c>, <c>cu^rl</c>
    /// and a name continued on the next line are all seen, as are <c>!curl</c>, <c>=curl</c> and <c>C:\tools\curl.exe</c>.
    /// </summary>
    public static bool NamesProgram(string text, string program)
    {
        var unquoted = Without(text, "'\"`^")
            .Replace("\\\r\n", string.Empty, StringComparison.Ordinal)
            .Replace("\\\n", string.Empty, StringComparison.Ordinal);
        return HasWord(unquoted, program) || HasWord(Without(unquoted, "\\\r\n"), program);
    }

    /// <summary>Whether <paramref name="text"/> starts with <paramref name="word"/> as a whole word, case-insensitively.</summary>
    public static bool StartsWithWord(string text, string word) =>
        text.StartsWith(word, StringComparison.OrdinalIgnoreCase) && (text.Length == word.Length || !IsWordCharacter(text[word.Length]));

    private static bool HasWord(string text, string word)
    {
        for (var at = text.IndexOf(word, StringComparison.OrdinalIgnoreCase); at >= 0; at = text.IndexOf(word, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            var end = at + word.Length;
            if ((at == 0 || !IsWordCharacter(text[at - 1])) && (end == text.Length || !IsWordCharacter(text[end])))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The length of one of <paramref name="redirects"/> standing as a word of its own at <paramref name="at"/> in <paramref name="command"/>
    /// (after a blank; followed by the end, a blank or a separator), else 0. <c>2>&amp;1x</c>, <c>2>/dev/null/x</c> and <c>test2>&amp;1</c> are no redirect.
    /// </summary>
    private static int RedirectAt(string command, int at, IReadOnlyList<string> redirects)
    {
        if (at == 0 || command[at - 1] is not (' ' or '\t'))
        {
            return 0;
        }

        foreach (var token in redirects)
        {
            var end = at + token.Length;
            if (end <= command.Length
                && string.CompareOrdinal(command, at, token, 0, token.Length) == 0
                && (end == command.Length || command[end] is ' ' or '\t' || IsSeparator(command[end])))
            {
                return token.Length;
            }
        }

        return 0;
    }

    private static bool IsWordCharacter(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static bool IsSeparator(char c) => c is ';' or '|' or '&' or '\r' or '\n';

    private static bool IsPrintableAscii(char c) => c is >= ' ' and <= '~' or '\t';

    private static string Describe(char c) =>
        c is > ' ' and <= '~' ? "'" + c + "'" : "U+" + ((int)c).ToString("X4", CultureInfo.InvariantCulture);

    private static string Without(string text, string characters)
    {
        var kept = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (!characters.Contains(c, StringComparison.Ordinal))
            {
                kept.Append(c);
            }
        }

        return kept.ToString();
    }

    private static void Flush(List<string> parts, StringBuilder current)
    {
        var part = current.ToString().Trim();
        if (part.Length > 0)
        {
            parts.Add(part);
        }

        current.Clear();
    }
}
