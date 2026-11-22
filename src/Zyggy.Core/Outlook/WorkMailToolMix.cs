using System.Text.RegularExpressions;

namespace Zyggy.Core.Outlook;

/// <summary>
/// Spec 58 decision B (2026-11-22): on a work machine (<c>outlook.enabled</c>) no job may both read work mail and run code, so nothing a
/// job read can be sent or moved by code it wrote. Judged on the job's effective <c>allowed_tools</c> at intake, before the model starts.
/// </summary>
/// <remarks>
/// <para>
/// A job <b>reads work mail</b> when one of its rules can run a <c>zyggy outlook</c> verb: a <c>Bash</c> or <c>PowerShell</c> rule (any
/// case, any blank around it) that is bare, empty or <c>*</c>; whose program word holds a wildcard, an <c>=</c> (an environment prefix) or
/// names <c>zyggy</c> (a path or <c>zyggy.exe</c> included) with <c>outlook</c> or a wildcard after it. Each entry is judged whole and,
/// because Claude Code reads a comma list as several rules, each comma-separated piece too. Any MCP rule (<c>mcp__*</c>, any case) also
/// counts as reading work mail (Q22, Central 2026-11-22): a project MCP server on the work laptop may reach the mailbox. A job whose only
/// mail reader is one MCP rule, written alone, keeps it beside the file reads; anything else beside it (a code runner, <c>Write</c>/<c>Edit</c>,
/// a second MCP server, <c>Task</c>, a comma list) is refused.
/// </para>
/// <para>
/// Such a job may hold only <c>Read</c>, <c>Grep</c> and <c>Glob</c> rules (bare or with a path) and <c>Bash</c> rules for the
/// <c>zyggy outlook</c> read verbs (<see cref="ReadVerbs"/>), written exactly: no comma, no shell metacharacter, the program
/// <c>zyggy</c> or <c>zyggy.exe</c> by name. A wildcard may stand for the verb (<c>zyggy outlook *</c>, Central's brief job) only where it
/// can still start a read verb: <c>zyggy</c> refuses every send and move from a job (slice 7), so <c>zyggy outlook *</c> reads only.
/// Anything else beside a mail-reading rule (a code runner, <c>Write</c>/<c>Edit</c>, an MCP server, <c>Task</c>, <c>WebFetch</c>, a
/// <c>PowerShell</c> rule) is refused. A job that reads no work mail keeps its tools: that a code-running job could drive Outlook over
/// COM itself, or run <c>zyggy outlook</c> through an interpreter, is the accepted risk of decision B.
/// </para>
/// </remarks>
internal static partial class WorkMailToolMix
{
    /// <summary>The <c>zyggy outlook</c> verbs that only read (the read tools of slices 2–5).</summary>
    public static readonly IReadOnlyList<string> ReadVerbs = ["policy", "count", "list", "read", "brief", "brief-done", "drafts", "share"];

    private const string McpPrefix = "mcp__";

    private static readonly string[] FileReadTools = ["Read", "Grep", "Glob"];

    private static readonly string[] Programs = ["zyggy", "zyggy.exe"];

    /// <summary>Returns why a work machine refuses the job's rules, or <see langword="null"/> when they may run there.</summary>
    /// <param name="rules">The job's effective <c>allowed_tools</c>.</param>
    /// <returns>The refusal detail, naming the mail-reading rule and the first rule that does not belong beside it.</returns>
    public static string? Refusal(IReadOnlyList<string> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        string[] readers = [.. rules.Where(ReadsMail)];
        if (readers.Length == 0)
        {
            return null;
        }

        // Q22: a lone MCP rule that is the job's only mail reader may stand beside the file reads; it is the reader, not a second tool.
        string reads = readers[0];
        string[] others = [.. rules.Where(r => !MailOnly(r) && !(readers.Length == 1 && IsMcp(r)))];
        if (others.Length == 0)
        {
            return null;
        }

        string code = others.FirstOrDefault(r => !string.Equals(r, reads, StringComparison.Ordinal)) ?? others[0];
        return $"allowed_tools can read work mail ({reads}) and run code ({code}) on a work machine";
    }

    private static bool ReadsMail(string entry) =>
        entry.Split(',').Append(entry).Any(piece => LooseRule().Match(piece) is { Success: true } m
            && m.Groups["tool"].Value is var tool
            && (tool.StartsWith(McpPrefix, StringComparison.OrdinalIgnoreCase)
                || ((tool.Equals("Bash", StringComparison.OrdinalIgnoreCase) || tool.Equals("PowerShell", StringComparison.OrdinalIgnoreCase))
                    && ShellReadsMail(m.Groups["spec"].Success ? m.Groups["spec"].Value : null))));

    // One MCP rule, written alone (no comma list).
    private static bool IsMcp(string entry) =>
        !entry.Contains(',', StringComparison.Ordinal)
        && LooseRule().Match(entry) is { Success: true } m
        && m.Groups["tool"].Value.StartsWith(McpPrefix, StringComparison.OrdinalIgnoreCase);

    private static bool ShellReadsMail(string? specifier)
    {
        var spec = specifier?.Trim() ?? string.Empty;
        if (spec.Length == 0 || spec == "*")
        {
            return true;
        }

        var blank = spec.IndexOfAny([' ', '\t']);
        var program = blank < 0 ? spec : spec[..blank];
        if (program.Contains('*', StringComparison.Ordinal) || program.Contains('=', StringComparison.Ordinal))
        {
            return true;
        }

        var name = program.Trim('"', '\'', '&').Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        var rest = blank < 0 ? string.Empty : spec[blank..];
        return name.Contains("zyggy", StringComparison.OrdinalIgnoreCase)
            && (rest.Contains("outlook", StringComparison.OrdinalIgnoreCase) || rest.Contains('*', StringComparison.Ordinal));
    }

    private static bool MailOnly(string entry)
    {
        if (entry.Contains(',', StringComparison.Ordinal) || Jobs.Hook.ToolRule.Parse(entry) is not { } rule)
        {
            return false;
        }

        if (FileReadTools.Contains(rule.Tool, StringComparer.Ordinal))
        {
            return true;
        }

        return rule.Tool == "Bash" && rule.Specifier is { } spec && ReadVerbCommand(spec);
    }

    // Whether a Bash pattern can only ever match "zyggy outlook <read verb> …" (a ":*" ending is "starts with", as a "*").
    private static bool ReadVerbCommand(string spec)
    {
        if (spec.IndexOfAny([';', '&', '|', '`', '$', '<', '>', '(', ')', '\n', '\r', '\t', '"', '\'', '\\']) >= 0)
        {
            return false;
        }

        if (spec.EndsWith(":*", StringComparison.Ordinal))
        {
            spec = spec[..^2] + "*";
        }

        var prefix = Programs.Select(p => p + " outlook ").FirstOrDefault(p => spec.StartsWith(p, StringComparison.Ordinal));
        if (prefix is null)
        {
            return false;
        }

        var rest = spec[prefix.Length..];
        var star = rest.IndexOf('*', StringComparison.Ordinal);
        var literal = star < 0 ? rest : rest[..star];
        var blank = literal.IndexOf(' ', StringComparison.Ordinal);
        if (blank >= 0 || star < 0)
        {
            // The verb is written out: it must be a read verb.
            var verb = blank >= 0 ? literal[..blank] : literal;
            return ReadVerbs.Contains(verb, StringComparer.Ordinal);
        }

        // A wildcard inside the verb: what is written must start a read verb.
        return ReadVerbs.Any(v => v.StartsWith(literal, StringComparison.Ordinal));
    }

    [GeneratedRegex(@"^\s*(?<tool>[A-Za-z0-9_\-]+)\s*(\((?<spec>.*)\))?\s*$", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex LooseRule();
}
