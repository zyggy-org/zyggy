using System.Text.Json;
using System.Text.RegularExpressions;

namespace Zyggy.Core.Jobs.Hook;

/// <summary>
/// The job hook's three checks, in order (spec 15 Behavior "The job hook"): the call matches an <c>allowed_tools</c> rule (a shell
/// pattern rule only when the command is simple, <see cref="ShellCommand.WhyNotSimple"/>); a file-tool write stays inside the run
/// directory and out of every <c>.git/</c>, <c>.claude/settings*.json</c> and <c>.claude/hooks/</c> in it, at any depth, under any
/// spelling; a shell command names no network program, however it is quoted, unless the part naming it matches a rule for that program.
/// With a read root (deliverable 18) a fourth check follows the first: a file-tool read, and a shell command naming a path, stay
/// inside the listed folders and files.
/// </summary>
internal sealed class JobHookPolicy
{
    private static readonly string[] NetworkPrograms = ["curl", "wget", "Invoke-WebRequest", "iwr", "Invoke-RestMethod", "irm"];

    private readonly string _root;
    private readonly IReadOnlyList<ToolRule> _rules;
    private readonly bool _ignoreCase;
    private readonly StringComparison _pathComparison;
    private readonly Regex _protected;
    private readonly IReadOnlyList<string> _readRoots;

    /// <param name="root">The run directory: the worktree for a worktree job, else the project folder; absolute.</param>
    /// <param name="rules">The effective <c>allowed_tools</c>.</param>
    /// <param name="ignoreCase">Whether paths compare without case (Windows, macOS).</param>
    /// <param name="readRoots">The read roots (deliverable 18): absolute folders or files; empty or <see langword="null"/> leaves reads unrestricted.</param>
    public JobHookPolicy(string root, IEnumerable<string> rules, bool ignoreCase, IEnumerable<string>? readRoots = null)
    {
        _readRoots = [.. (readRoots ?? []).Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r)))];
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _rules = [.. rules.Select(ToolRule.Parse).OfType<ToolRule>()];
        _ignoreCase = ignoreCase;
        _pathComparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        _protected = new Regex(
            @"(^|/)(\.git(/.*)?|\.claude/settings[^/]*\.json|\.claude/hooks(/.*)?)$",
            RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None),
            TimeSpan.FromSeconds(1));
    }

    /// <summary>Decides one tool call.</summary>
    /// <param name="tool">The input's <c>tool_name</c>.</param>
    /// <param name="input">The input's <c>tool_input</c> object.</param>
    /// <param name="cwd">The input's <c>cwd</c>; relative paths resolve against it, else against the run directory.</param>
    public HookDecision Decide(string tool, JsonElement input, string? cwd)
    {
        var shell = tool is "Bash" or "PowerShell";
        var command = shell ? Text(input, "command") ?? string.Empty : string.Empty;
        var path = PathOf(tool, input);

        // A pattern rule vouches only for a simple command; one that is not simple is matched, and scanned, as a whole.
        var notSimple = shell ? ShellCommand.WhyNotSimple(command, ShellCommand.DialectOf(tool)) : null;
        IReadOnlyList<string> parts = !shell ? [] : notSimple is null ? ShellCommand.Parts(command) : [command];

        if (!Allowed(tool, input, parts, notSimple is not null, path, cwd))
        {
            var detail = !shell ? path ?? tool : notSimple is null ? command : $"not a simple command ({notSimple}): {command}";
            return HookDecision.Deny(HookDecision.NotAllowedRule, detail);
        }

        if (_readRoots.Count > 0 && ReadOutside(tool, input, shell, command, notSimple, parts, path, cwd) is { } outside)
        {
            return HookDecision.Deny(HookDecision.ReadOutsideRule, outside);
        }

        if (tool is "Write" or "Edit" or "MultiEdit" or "NotebookEdit" && !WritableInside(path, cwd))
        {
            return HookDecision.Deny(HookDecision.WriteOutsideRule, path ?? "no path");
        }

        if (shell && NetworkProgramIn(tool, parts))
        {
            return HookDecision.Deny(HookDecision.NetworkRule, command);
        }

        return HookDecision.Allow;
    }

    private static string? Text(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? PathOf(string tool, JsonElement input) => tool switch
    {
        "NotebookEdit" => Text(input, "notebook_path"),
        "Glob" or "Grep" => Text(input, "path"),
        _ => Text(input, "file_path"),
    };

    private bool Allowed(string tool, JsonElement input, IReadOnlyList<string> parts, bool notSimple, string? path, string? cwd)
    {
        var named = _rules.Where(r => r.Names(tool)).ToList();
        if (named.Count == 0)
        {
            return false;
        }

        if (named.Exists(r => r.Specifier is null))
        {
            return true;
        }

        switch (tool)
        {
            case "Bash" or "PowerShell":
                return notSimple
                    ? named.Exists(r => r.Specifier == "*")
                    : parts.Count > 0 && parts.All(part => named.Exists(r => r.MatchesCommand(part)));
            case "Read" or "Edit" or "Write" or "Glob" or "Grep":
                return Relative(path, cwd) is { } relative && named.Exists(r => r.MatchesPath(relative, _ignoreCase));
            case "WebFetch":
                return Text(input, "url") is { } url && named.Exists(r => r.MatchesUrl(url));
            default:
                return false;
        }
    }

    // The path relative to the run directory with forward slashes ("" for the directory itself); null when it lies outside.
    private string? Relative(string? path, string? cwd)
    {
        if (Full(path ?? string.Empty, cwd) is not { } full)
        {
            return null;
        }

        if (string.Equals(full, _root, _pathComparison))
        {
            return string.Empty;
        }

        var prefix = _root + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, _pathComparison) ? full[prefix.Length..].Replace('\\', '/') : null;
    }

    private string? Full(string path, string? cwd)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path, cwd is { Length: > 0 } && Path.IsPathFullyQualified(cwd) ? cwd : _root));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    // Deliverable 18, the read root: what the call would read outside every root, or null when it stays inside. A file-tool read
    // is judged by its path (a search without one reads the run directory). A shell command is judged word by word: an argument is
    // a path when it has no blank and holds a path separator or starts with '~' or '.', or when it exists on disk; a command that
    // is not simple cannot be read and is refused whole. Links are not followed, as for the write rule.
    private string? ReadOutside(string tool, JsonElement input, bool shell, string command, string? notSimple, IReadOnlyList<string> parts, string? path, string? cwd)
    {
        if (tool is "Read" or "Grep" or "Glob" or "NotebookRead")
        {
            var target = path ?? (tool == "Read" ? null : ".");
            if (target is null || !InsideReadRoot(Full(target, cwd)))
            {
                return path ?? "no path";
            }

            // A search pattern is part of what is read: one that climbs out of its folder, or names another place, is refused.
            foreach (var name in new[] { "pattern", "glob" })
            {
                if (tool != "Read" && !(tool == "Grep" && name == "pattern") && Text(input, name) is { } pattern && LeavesItsFolder(pattern))
                {
                    return pattern;
                }
            }

            return null;
        }

        if (!shell)
        {
            return null;
        }

        if (notSimple is not null)
        {
            return $"not a simple command ({notSimple}): {command}";
        }

        foreach (var part in parts)
        {
            foreach (var (word, blank) in ShellCommand.Words(part).Skip(1))
            {
                // An option's value: --file=<path>.
                var equals = word.StartsWith('-') ? word.IndexOf('=', StringComparison.Ordinal) : -1;
                var candidate = equals >= 0 ? word[(equals + 1)..] : word;
                if (candidate.Length == 0)
                {
                    continue;
                }

                // A quoted sentence with blanks is text, unless it exists on disk: then it is a path with blanks in it.
                var full = candidate.StartsWith('~') ? null : Full(candidate, cwd);
                var shaped = !blank
                    && (candidate.Contains('/', StringComparison.Ordinal) || candidate.Contains('\\', StringComparison.Ordinal) || candidate[0] is '~' or '.');
                if ((shaped || (full is not null && (File.Exists(full) || Directory.Exists(full)))) && !InsideReadRoot(full))
                {
                    return command;
                }
            }
        }

        return null;
    }

    private static bool LeavesItsFolder(string pattern) =>
        pattern.Split('/', '\\').Contains("..") || pattern.StartsWith('~') || Path.IsPathRooted(pattern);

    private bool InsideReadRoot(string? full) =>
        full is not null && _readRoots.Any(root =>
            string.Equals(full, root, _pathComparison) || full.StartsWith(root + Path.DirectorySeparatorChar, _pathComparison));

    private bool WritableInside(string? path, string? cwd)
    {
        if (path is not { Length: > 0 } || Relative(path, cwd) is not { Length: > 0 } relative)
        {
            return false;
        }

        // A colon names an alternate stream on Windows ("settings.json::$DATA", ".git::$INDEX_ALLOCATION"), and a segment ending in a
        // dot or a space is trimmed there to another name (".git /config"): both would reach a protected file under another spelling.
        // Refused on every system, for one rule.
        if (relative.Contains(':', StringComparison.Ordinal)
            || path.Split('/', '\\').Any(segment => segment is not ("." or "..") && (segment.EndsWith('.') || segment.EndsWith(' '))))
        {
            return false;
        }

        return !_protected.IsMatch(relative);
    }

    // A part names a network program unless that part itself matches a rule of this tool whose pattern starts with the program.
    private bool NetworkProgramIn(string tool, IReadOnlyList<string> parts) =>
        parts.Any(part => NetworkPrograms.Any(program =>
            ShellCommand.NamesProgram(part, program)
            && !_rules.Any(r => r.Names(tool) && r.Specifier is { } s && ShellCommand.StartsWithWord(s, program) && r.MatchesCommand(part))));
}
