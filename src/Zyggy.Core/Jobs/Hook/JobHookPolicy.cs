using System.Text.Json;
using System.Text.RegularExpressions;

namespace Zyggy.Core.Jobs.Hook;

/// <summary>
/// The job hook's three checks, in order (spec 15 Behavior "The job hook"): the call matches an <c>allowed_tools</c> rule (a shell
/// pattern rule only when the command is simple, <see cref="ShellCommand.WhyNotSimple"/>); a file-tool write stays inside the run
/// directory and out of every <c>.git/</c>, <c>.claude/settings*.json</c> and <c>.claude/hooks/</c> in it, at any depth, under any
/// spelling; a shell command names no network program, however it is quoted, unless the part naming it matches a rule for that program.
/// </summary>
internal sealed class JobHookPolicy
{
    private static readonly string[] NetworkPrograms = ["curl", "wget", "Invoke-WebRequest", "iwr", "Invoke-RestMethod", "irm"];

    private readonly string _root;
    private readonly IReadOnlyList<ToolRule> _rules;
    private readonly bool _ignoreCase;
    private readonly StringComparison _pathComparison;
    private readonly Regex _protected;

    /// <param name="root">The run directory: the worktree for a worktree job, else the project folder; absolute.</param>
    /// <param name="rules">The effective <c>allowed_tools</c>.</param>
    /// <param name="ignoreCase">Whether paths compare without case (Windows, macOS).</param>
    public JobHookPolicy(string root, IEnumerable<string> rules, bool ignoreCase)
    {
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
