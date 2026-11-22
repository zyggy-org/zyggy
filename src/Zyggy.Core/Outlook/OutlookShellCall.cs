using System.Text;
using System.Text.RegularExpressions;

namespace Zyggy.Core.Outlook;

/// <summary>What a shell command is to the outlook guard (spec 58 slice 7).</summary>
internal enum OutlookShellCallKind
{
    /// <summary>It names no outlook action.</summary>
    None,

    /// <summary>It is exactly one plain <c>zyggy outlook send|move …</c> call.</summary>
    Action,

    /// <summary>It names an outlook action but is not one plain call: refused whole.</summary>
    Refused,
}

/// <summary>
/// The outlook guard's reading of one <c>Bash</c> command (spec 58 slice 7, AC-11, AC-12). A command that names an outlook action
/// (<see cref="ActionNamed"/>, however quoted or prefixed) is accepted only as exactly one plain call — a program word that is
/// <c>zyggy</c> (or a path to it), <c>outlook</c>, <c>send</c> or <c>move</c>, then words built from plain characters, single quotes,
/// double quotes with bash's escapes and backslash escapes. No variable, substitution, glob, brace, tilde, comment, redirect, second
/// command or environment prefix: what the guard checks and the prompt shows is what bash runs.
/// </summary>
/// <param name="Kind">What the command is.</param>
/// <param name="Arguments">For <see cref="OutlookShellCallKind.Action"/>, the arguments after <c>outlook</c>; else empty.</param>
/// <param name="Problem">For <see cref="OutlookShellCallKind.Refused"/>, why.</param>
/// <param name="Action">The action the command names (<c>send</c> or <c>move</c>), or <see langword="null"/>.</param>
internal sealed partial record OutlookShellCall(OutlookShellCallKind Kind, IReadOnlyList<string> Arguments, string? Problem, string? Action)
{
    // Unquoted, these change what bash runs: expansions, globs, braces, tilde, comment, history, grouping, redirects, separators.
    private const string Special = "$`*?[]{}~#!()<>;&|";

    /// <summary>Reads a Bash command.</summary>
    /// <param name="command">The command text.</param>
    /// <returns>The reading.</returns>
    public static OutlookShellCall Read(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (ActionNamed(command) is not { } action)
        {
            return new OutlookShellCall(OutlookShellCallKind.None, [], null, null);
        }

        var (words, problem) = Words(command);
        if (words is null)
        {
            return Refused(problem!, action);
        }

        if (words.Count < 3 || !IsZyggy(words[0]) || words[1] != "outlook" || words[2] is not ("send" or "move"))
        {
            return Refused("not one plain zyggy outlook " + action + " call", action);
        }

        return new OutlookShellCall(OutlookShellCallKind.Action, words.Skip(2).ToList(), null, words[2]);
    }

    /// <summary>
    /// Returns the outlook action a command names, however it is quoted or escaped (quotes, backslashes, carets, backticks and
    /// <c>$</c> are ignored, case too): <c>zyggy</c> or <c>zyggy.exe</c>, then <c>outlook</c>, then <c>send</c> or <c>move</c>.
    /// </summary>
    /// <param name="command">The command text.</param>
    /// <returns><c>send</c>, <c>move</c>, or <see langword="null"/>.</returns>
    public static string? ActionNamed(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var plain = new StringBuilder(command.Length);
        foreach (char c in command)
        {
            // '$' too: bash's $'…' and $"…" quoting spell a plain word.
            if (c is not ('\'' or '"' or '\\' or '^' or '`' or '$'))
            {
                plain.Append(c);
            }
        }

        var match = Named().Match(plain.ToString());
        return match.Success ? match.Groups["a"].Value.ToLowerInvariant() : null;
    }

    /// <summary>
    /// The job hook's wider test (verify 01M81R40G1YGA46142HYTV5H7T point 4): whether a shell command may be an outlook write in any
    /// form. Escapes are decoded first (<c>\xHH</c>, <c>\uHHHH</c>, <c>\UHHHHHHHH</c>, octal <c>\NNN</c>, PowerShell <c>`u{HHHH}</c>);
    /// then only the letters count, lowercased, so quotes, concatenation and blanks split nothing. It may be one when it mentions
    /// <c>outlook</c> followed anywhere by <c>send</c> or <c>move</c>; or mentions <c>outlook</c> together with anything that can build
    /// a word at run time (a variable or substitution, an escape, <c>[char]</c>, <c>eval</c>, <c>base64</c>, …); or mentions
    /// <c>zyggy</c> with <c>send</c> or <c>move</c> and such a construct. It refuses more than it must, never less for those forms; a
    /// name hidden wholly (in a script file, or built without any of those words) is the known limit, closed by the tool's own
    /// refusal in a job and by the guard's nonce.
    /// </summary>
    /// <param name="command">The command text.</param>
    /// <returns>Whether the job hook refuses the command.</returns>
    public static bool MayWriteOutlook(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        string letters = string.Concat(Decode(command).Where(char.IsLetter).Select(char.ToLowerInvariant));
        int outlook = letters.IndexOf("outlook", StringComparison.Ordinal);
        bool zyggy = letters.Contains("zyggy", StringComparison.Ordinal);
        if (outlook < 0 && !zyggy)
        {
            return false;
        }

        bool built = Built().IsMatch(command);
        bool verb = WriteVerb().IsMatch(letters);
        return (outlook >= 0 && (built || WriteVerb().IsMatch(letters[outlook..]))) || (zyggy && verb && built);
    }

    // Decodes the escapes bash ($'…', printf, echo -e) and PowerShell spell a character with.
    private static string Decode(string command) => Escape().Replace(command, m =>
    {
        string hex = m.Groups["hex"].Success ? m.Groups["hex"].Value : string.Empty;
        int code = m.Groups["oct"].Success
            ? Convert.ToInt32(m.Groups["oct"].Value, 8)
            : int.Parse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
        return code is > 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF) ? char.ConvertFromUtf32(code) : string.Empty;
    });

    [GeneratedRegex(@"\\(?:x(?<hex>[0-9A-Fa-f]{1,2})|u(?<hex>[0-9A-Fa-f]{1,4})|U(?<hex>[0-9A-Fa-f]{1,8})|(?<oct>0?[0-7]{1,3}))|`u\{(?<hex>[0-9A-Fa-f]{1,6})\}", RegexOptions.CultureInvariant)]
    private static partial Regex Escape();

    [GeneratedRegex(@"send|move", RegexOptions.CultureInvariant)]
    private static partial Regex WriteVerb();

    // What builds a word at run time: expansions and substitutions ($, backtick, %VAR%), escapes, and the commands and operators
    // that decode, reverse or assemble text.
    [GeneratedRegex(@"[$`%*?]|\\[0-7xuUc]|\[char\]|-join|-replace|-f\s|\b(?:eval|iex|invoke-expression|base64|frombase64string|printf|xxd|rev|tr|sed|awk|perl|python3?|node)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Built();

    private static OutlookShellCall Refused(string problem, string action) => new(OutlookShellCallKind.Refused, [], problem, action);

    private static bool IsZyggy(string program)
    {
        string name = program[(program.LastIndexOfAny(['/', '\\']) + 1)..];
        return name is "zyggy" or "zyggy.exe";
    }

    // Splits the command into bash words, or says why it is not one plain command.
    private static (List<string>? Words, string? Problem) Words(string command)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        var inWord = false;
        for (var i = 0; i < command.Length; i++)
        {
            char c = command[i];
            switch (c)
            {
                case ' ' or '\t':
                    if (inWord)
                    {
                        words.Add(word.ToString());
                        word.Clear();
                        inWord = false;
                    }

                    continue;
                case '\r' or '\n':
                    return (null, "more than one line");
                case '\'':
                    int close = command.IndexOf('\'', i + 1);
                    if (close < 0)
                    {
                        return (null, "an unclosed quote");
                    }

                    word.Append(command, i + 1, close - i - 1);
                    i = close;
                    inWord = true;
                    continue;
                case '"':
                    int end = DoubleQuoted(command, i + 1, word);
                    if (end < 0)
                    {
                        return (null, end == -1 ? "an unclosed quote" : "an expansion inside double quotes");
                    }

                    i = end;
                    inWord = true;
                    continue;
                case '\\':
                    if (i + 1 >= command.Length || command[i + 1] is '\r' or '\n')
                    {
                        return (null, "a line continuation");
                    }

                    word.Append(command[++i]);
                    inWord = true;
                    continue;
                default:
                    if (Special.Contains(c, StringComparison.Ordinal) || char.IsControl(c))
                    {
                        return (null, "shell syntax '" + c + "'");
                    }

                    word.Append(c);
                    inWord = true;
                    continue;
            }
        }

        if (inWord)
        {
            words.Add(word.ToString());
        }

        return (words, null);
    }

    // Bash double quotes: \ escapes only $ ` " \ (a newline continuation is refused); an unescaped $ or ` is an expansion.
    // Returns the closing quote's index, -1 when unclosed, -2 for an expansion.
    private static int DoubleQuoted(string command, int start, StringBuilder word)
    {
        for (var i = start; i < command.Length; i++)
        {
            char c = command[i];
            if (c == '"')
            {
                return i;
            }

            if (c is '$' or '`')
            {
                return -2;
            }

            if (c == '\\' && i + 1 < command.Length && command[i + 1] is '$' or '`' or '"' or '\\')
            {
                word.Append(command[++i]);
                continue;
            }

            if (c == '\\' && i + 1 < command.Length && command[i + 1] is '\r' or '\n')
            {
                return -2;
            }

            word.Append(c);
        }

        return -1;
    }

    [GeneratedRegex(@"(?i)zyggy(?:\.exe)?\s+outlook\s+(?<a>send|move)\b", RegexOptions.CultureInvariant)]
    private static partial Regex Named();
}
