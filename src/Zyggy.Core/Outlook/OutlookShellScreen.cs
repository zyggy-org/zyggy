using System.Text;
using System.Text.RegularExpressions;

namespace Zyggy.Core.Outlook;

/// <summary>
/// The outlook guard hook's screen of every tool call in an attended session on a work machine (verify 01M81W8C4K81GE119A7WH3228D
/// point 1, spec 58 slice 7). The guard reads a plain <c>zyggy outlook send|move …</c> call itself; this screen refuses what could get
/// around that reading in one call: a command naming the pass file, its folder or the approval key; an outlook action that is not one
/// plain call; <c>zyggy</c> run through a variable, substitution, escape, glob, alias or <c>eval</c>; an interpreter handed inline code
/// naming Outlook; or a script file that runs Outlook (or that the guard cannot read). It refuses more than it must; a name built
/// wholly at run time, or code that drives Outlook without naming it, is the residual risk of spec 58 Q24.
/// </summary>
internal static partial class OutlookShellScreen
{
    private const string NamesState = "names the outlook pass file, its folder or the approval key";

    /// <summary>Screens one shell command (Bash, PowerShell, or any tool carrying a <c>command</c>).</summary>
    /// <param name="command">The command text.</param>
    /// <param name="stateDirectory">The laptop's outlook state directory (the pass file and the approval keys).</param>
    /// <param name="readScript">Reads a script file named in the command, as written (relative to the session's directory); <see langword="null"/> when it cannot be read.</param>
    /// <returns>Why the command is refused, or <see langword="null"/> when it passes.</returns>
    public static string? Check(string command, string stateDirectory, Func<string, string?> readScript)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(stateDirectory);
        ArgumentNullException.ThrowIfNull(readScript);
        if (string.IsNullOrWhiteSpace(command) || OutlookShellCall.Read(command).Kind == OutlookShellCallKind.Action)
        {
            // Empty, or exactly one plain outlook call: the guard itself reads and judges that one.
            return null;
        }

        string decoded = OutlookShellCall.Decode(command);
        string paths = PathForm(decoded);
        string words = WordForm(decoded);
        if (paths.Contains(PathForm(stateDirectory), StringComparison.Ordinal) || StateNamed().IsMatch(paths) || Tokens(paths).Any(NearState))
        {
            return NamesState;
        }

        if (OutlookShellCall.MayWriteOutlook(command))
        {
            return "an outlook action that is not one plain zyggy outlook call";
        }

        if (RunsZyggy(words, paths) && (OutlookShellCall.Built().IsMatch(command) || Indirect().IsMatch(words)))
        {
            return "zyggy run through a variable, substitution, escape, glob, alias or eval";
        }

        if (Interpreter().IsMatch(paths) && Letters(decoded).Contains("outlook", StringComparison.Ordinal))
        {
            return "an interpreter with code naming outlook";
        }

        return Scripts(command, readScript);
    }

    /// <summary>Screens a path a tool reads or writes (<c>file_path</c>, <c>path</c>, <c>notebook_path</c>).</summary>
    /// <param name="path">The path.</param>
    /// <param name="stateDirectory">The laptop's outlook state directory.</param>
    /// <returns>Why the call is refused, or <see langword="null"/>.</returns>
    public static string? CheckPath(string path, string stateDirectory)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(stateDirectory);
        string value = PathForm(path).TrimEnd('/');
        string state = PathForm(stateDirectory).TrimEnd('/');
        return value == state || value.StartsWith(state + "/", StringComparison.Ordinal) ? NamesState : null;
    }

    // Lowercase, quotes and carets gone, '$' gone (so $env:X and "$X" read as names), backslashes as '/', runs of '/' as one.
    private static string PathForm(string text)
    {
        var form = new StringBuilder(text.Length);
        foreach (char c in text.ToLowerInvariant())
        {
            char d = c == '\\' ? '/' : c;
            if (d is '\'' or '"' or '`' or '^' or '$' || (d == '/' && form.Length > 0 && form[^1] == '/'))
            {
                continue;
            }

            form.Append(d);
        }

        return form.ToString();
    }

    // As bash reads a word: quotes, escapes, carets and '$' dropped, so z\yggy and z''yggy read zyggy.
    private static string WordForm(string text) =>
        string.Concat(text.ToLowerInvariant().Where(c => c is not ('\'' or '"' or '`' or '^' or '$' or '\\')));

    private static string Letters(string text) => string.Concat(text.Where(char.IsLetter).Select(char.ToLowerInvariant));

    private static IEnumerable<string> Tokens(string text) => Separators().Split(text).Where(t => t.Length > 0);

    // A glob next to the state folder's names: work/*/out*/*, pend*, approval*.key.
    private static bool NearState(string token) =>
        token.IndexOfAny(['*', '?', '[']) >= 0
        && (token.Contains("work/", StringComparison.Ordinal) || token.Contains("outl", StringComparison.Ordinal) || token.Contains("pend", StringComparison.Ordinal)
            || token.Contains("appr", StringComparison.Ordinal) || token.Contains(".key", StringComparison.Ordinal));

    // zyggy as a program: the word zyggy or zyggy.exe (not a folder or a file like Zyggy.slnx), or a glob that matches it.
    private static bool RunsZyggy(string words, string paths) =>
        Program().IsMatch(words) || Tokens(paths).Any(t => t.IndexOfAny(['*', '?', '[']) >= 0 && GlobMatchesZyggy(t[(t.LastIndexOf('/') + 1)..]));

    private static bool GlobMatchesZyggy(string glob)
    {
        var pattern = new StringBuilder("^");
        foreach (char c in glob)
        {
            pattern.Append(c switch
            {
                '*' => ".*",
                '?' => ".",
                '[' or ']' or '!' or '-' => c.ToString(),
                _ => Regex.Escape(c.ToString()),
            });
        }

        try
        {
            var regex = new Regex(pattern.Append('$').ToString(), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            return regex.IsMatch("zyggy") || regex.IsMatch("zyggy.exe");
        }
        catch (ArgumentException)
        {
            // A glob the guard cannot read could be anything.
            return true;
        }
    }

    // A script the command runs: its first word when that is a script path, or the first plain word after an interpreter.
    private static string? Scripts(string command, Func<string, string?> readScript)
    {
        string unquoted = string.Concat(command.Where(c => c is not ('\'' or '"')));
        foreach (string segment in Segments().Split(unquoted))
        {
            string[] parts = segment.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            string? script = null;
            var mustRead = false;
            if (IsScriptPath(parts[0]))
            {
                (script, mustRead) = (parts[0], true);
            }
            else if (InterpreterWord().IsMatch(Path.GetFileName(parts[0].Replace('\\', '/'))))
            {
                script = parts.Skip(1).FirstOrDefault(p => !p.StartsWith('-') && !p.StartsWith('/'));
                mustRead = script is not null && IsScriptPath(script);
            }

            if (script is null || IsZyggyName(script))
            {
                continue;
            }

            string? content = readScript(script);
            if (content is null)
            {
                if (mustRead)
                {
                    return "a script file the guard cannot read";
                }

                continue;
            }

            if (!content.Contains('\0', StringComparison.Ordinal)
                && (Letters(OutlookShellCall.Decode(content)).Contains("outlook", StringComparison.Ordinal) || OutlookShellCall.MayWriteOutlook(content)))
            {
                return "a script file that runs outlook";
            }
        }

        return null;
    }

    private static bool IsScriptPath(string word) =>
        word.StartsWith("./", StringComparison.Ordinal) || word.StartsWith("../", StringComparison.Ordinal) || word.StartsWith(".\\", StringComparison.Ordinal)
        || ScriptExtension().IsMatch(word);

    private static bool IsZyggyName(string word)
    {
        string name = Path.GetFileName(word.Replace('\\', '/')).ToLowerInvariant();
        return name is "zyggy" or "zyggy.exe";
    }

    [GeneratedRegex(@"work/[^/\s]+/outlook(?![\w.-])|pending\.(?:jsonl|lock)|pending\.?[*?\[]|approval[^\s/]*\.key|approval[-_]?[*?\[]|(?:^|[\s;&|(])(?:cd|pushd|chdir|set-location|sl)\s+outlook(?![\w.-])", RegexOptions.CultureInvariant)]
    private static partial Regex StateNamed();

    [GeneratedRegex(@"zyggy(?:\.exe)?(?![\w./-])", RegexOptions.CultureInvariant)]
    private static partial Regex Program();

    [GeneratedRegex(@"(?<![\w/.\\-])(?:alias|function|source|eval|exec|xargs|env|command|iex|invoke-expression|invoke-command|start-process|set-alias|new-alias|get-command|call|start|sudo|nohup)(?![\w/.\\-])|(?:^|[\s;&|])\.\s", RegexOptions.CultureInvariant)]
    private static partial Regex Indirect();

    [GeneratedRegex(@"(?:^|[\s;&|(/])(?:python[0-9.]*|py|node|deno|bun|perl|ruby|php|pwsh|powershell|cscript|wscript|mshta|osascript|bash|sh|zsh|cmd|csi)(?:\.exe)?(?=\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex Interpreter();

    [GeneratedRegex(@"\A(?:python[0-9.]*|py|node|deno|bun|perl|ruby|php|pwsh|powershell|cscript|wscript|bash|sh|zsh|ksh|dash|source|\.)(?:\.exe)?\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex InterpreterWord();

    [GeneratedRegex(@"\.(?:sh|bash|zsh|ksh|ps1|psm1|cmd|bat|py|js|mjs|cjs|ts|pl|rb|vbs|php)\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptExtension();

    [GeneratedRegex(@"[\s;&|()<>=,]+", RegexOptions.CultureInvariant)]
    private static partial Regex Separators();

    [GeneratedRegex(@"[;&|()\r\n]+", RegexOptions.CultureInvariant)]
    private static partial Regex Segments();
}
