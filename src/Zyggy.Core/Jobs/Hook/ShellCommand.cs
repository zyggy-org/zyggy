using System.Text;

namespace Zyggy.Core.Jobs.Hook;

/// <summary>
/// The shell text of a <c>Bash</c> or <c>PowerShell</c> tool call, read the way the job hook needs it (spec 15): its parts
/// (split on <c>&amp;&amp;</c>, <c>||</c>, <c>;</c>, <c>|</c>, a lone <c>&amp;</c> and line breaks outside quotes), whether a
/// part holds command or process substitution, and the words of the whole text.
/// </summary>
internal static class ShellCommand
{
    private static readonly char[] WordBreaks = [' ', '\t', '\r', '\n', '(', ')', '{', '}', '[', ']', ';', '|', '&', '`', '$', '\'', '"', '<', '>', ',', '='];

    /// <summary>Splits <paramref name="command"/> into its trimmed, non-empty parts.</summary>
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
                if (c == '\\' && quote == '"' && i + 1 < command.Length)
                {
                    current.Append(command[++i]);
                }
                else if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            switch (c)
            {
                case '\'' or '"':
                    quote = c;
                    current.Append(c);
                    break;
                case ';' or '\n' or '\r':
                    Flush(parts, current);
                    break;
                case '|':
                    Flush(parts, current);
                    if (i + 1 < command.Length && command[i + 1] == '|')
                    {
                        i++;
                    }

                    break;
                case '&' when IsRedirection(command, i):
                    current.Append(c);
                    break;
                case '&':
                    Flush(parts, current);
                    if (i + 1 < command.Length && command[i + 1] == '&')
                    {
                        i++;
                    }

                    break;
                default:
                    current.Append(c);
                    break;
            }
        }

        Flush(parts, current);
        return parts;
    }

    /// <summary>Whether <paramref name="part"/> holds <c>$(</c>, a backtick, <c>&lt;(</c> or <c>&gt;(</c> outside single quotes.</summary>
    public static bool HasSubstitution(string part)
    {
        var single = false;
        var doubled = false;
        for (var i = 0; i < part.Length; i++)
        {
            var c = part[i];
            if (c == '\'' && !doubled)
            {
                single = !single;
                continue;
            }

            if (c == '"' && !single)
            {
                doubled = !doubled;
                continue;
            }

            if (single)
            {
                continue;
            }

            if (c == '`')
            {
                return true;
            }

            if (c is '$' or '<' or '>' && i + 1 < part.Length && part[i + 1] == '(')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Every word of <paramref name="command"/>, split on whitespace, brackets, operators and quotes.</summary>
    public static IEnumerable<string> Words(string command) =>
        command.Split(WordBreaks, StringSplitOptions.RemoveEmptyEntries);

    // "2>&1", "&>", ">&": the ampersand belongs to a redirection, not a separator.
    private static bool IsRedirection(string command, int i) =>
        (i > 0 && command[i - 1] is '>' or '<') || (i + 1 < command.Length && command[i + 1] == '>');

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
