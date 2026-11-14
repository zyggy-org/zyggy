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
/// language has no escapes, so three states (unquoted, single-quoted, double-quoted) read it exactly.
/// </summary>
internal static class ShellCommand
{
    private const string UnquotedPunctuation = " \t-_./:=,+%;|&\r\n";

    /// <summary>The dialect of <paramref name="tool"/>'s shell.</summary>
    public static ShellDialect DialectOf(string tool) => tool == "PowerShell" ? ShellDialect.PowerShell : ShellDialect.Bash;

    /// <summary>
    /// Why <paramref name="command"/> is not a simple command, or <see langword="null"/> when it is one. Unquoted: ASCII letters and
    /// digits, space, tab, <c>- _ . / : = , + %</c>, the separators <c>; | &amp;</c> and line breaks, <c>@</c> in Bash, <c>\</c> in
    /// PowerShell. In single quotes: printable ASCII. In double quotes: printable ASCII without <c>$</c>, the backtick and, in Bash,
    /// the backslash. No line break inside a quote, no quote left open, no <c>name=value</c> as the first word of a part.
    /// </summary>
    public static string? WhyNotSimple(string command, ShellDialect dialect)
    {
        var quote = '\0';
        var inFirstWord = true;
        var firstWordStarted = false;
        foreach (var c in command)
        {
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
                else if (!IsPrintableAscii(c) || (quote == '"' && (c is '$' or '`' || (c == '\\' && dialect == ShellDialect.Bash))))
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

    /// <summary>The trimmed, non-empty parts of a simple <paramref name="command"/>: split on <c>;</c>, <c>|</c>, <c>&amp;</c> and line breaks outside quotes.</summary>
    public static IReadOnlyList<string> Parts(string command)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var quote = '\0';
        foreach (var c in command)
        {
            if (quote != '\0')
            {
                current.Append(c);
                quote = c == quote ? '\0' : quote;
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
