using System.Text.RegularExpressions;

namespace Zyggy.Core.Outlook;

/// <summary>
/// Spec 58 decision B as corrected by the owner (2026-11-23): enabling Outlook on a work machine (<c>outlook.enabled</c>) must not affect
/// any other work there, and no job is refused for its rules. A <b>mail job</b> runs with the outlook read verbs and file reads only
/// (<see cref="MailRules"/>); every other job keeps its tools, and the job hook refuses its <c>zyggy outlook</c> calls (it gets no work
/// mail marker either, so the read tools refuse it).
/// </summary>
/// <remarks>
/// <para>
/// A job is a <b>mail job</b> only when one of its rules explicitly grants the outlook verbs: a <c>Bash</c> or <c>PowerShell</c> rule (any
/// case) whose program word, after any environment prefix, names <c>zyggy</c> (a path or <c>zyggy.exe</c> included) and whose rest names
/// <c>outlook</c>. Each entry is judged whole and, because Claude Code reads a comma list as several rules, each comma-separated piece
/// too. Generic rules — <c>Bash(src/*)</c>, <c>Bash(dotnet *)</c>, <c>Bash(node *)</c>, <c>Bash(zyggy *)</c>, bare <c>Bash</c>,
/// <c>Edit</c>, <c>Write</c>, an MCP server — never make a job a mail job.
/// </para>
/// <para>
/// A mail job may hold only <c>Read</c>, <c>Grep</c> and <c>Glob</c> rules (bare or with a path) and <c>Bash</c> rules for the
/// <c>zyggy outlook</c> read verbs (<see cref="ReadVerbs"/>), written exactly: no comma, no shell metacharacter, the program
/// <c>zyggy</c> or <c>zyggy.exe</c> by name. A wildcard may stand for the verb (<c>zyggy outlook *</c>, Central's brief job) only where it
/// can still start a read verb: <c>zyggy</c> refuses every send and move from a job (slice 7), so <c>zyggy outlook *</c> reads only.
/// Anything else beside it (a code runner, bare <c>Bash</c>, <c>Write</c>/<c>Edit</c>, an MCP server, <c>Task</c>, <c>WebFetch</c>, a
/// <c>PowerShell</c> rule) is dropped from the run. That a code job could drive Outlook over COM from code it wrote is the
/// accepted risk; the mail side flags mails that ask for code to be run (<see cref="WorkMailActionRequests"/>).
/// </para>
/// </remarks>
internal static partial class WorkMailToolMix
{
    /// <summary>The <c>zyggy outlook</c> verbs that only read (the read tools of slices 2–5).</summary>
    public static readonly IReadOnlyList<string> ReadVerbs = ["policy", "count", "list", "read", "brief", "brief-done", "drafts", "share"];

    private static readonly string[] FileReadTools = ["Read", "Grep", "Glob"];

    private static readonly string[] Programs = ["zyggy", "zyggy.exe"];

    /// <summary>
    /// The rules a mail job keeps on a work machine: its <c>Read</c>, <c>Grep</c>, <c>Glob</c> rules and its <c>Bash</c> rules for the
    /// outlook read verbs, in their order. Every other rule is dropped, so the job's tools and its hook never grant it; nothing is refused.
    /// </summary>
    /// <param name="rules">The mail job's effective <c>allowed_tools</c>.</param>
    /// <returns>The rules it keeps; empty when none is a read rule.</returns>
    public static IReadOnlyList<string> MailRules(IReadOnlyList<string> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return [.. rules.Where(MailOnly)];
    }

    /// <summary>Whether the rules make the job a mail job: one of them explicitly grants the <c>zyggy outlook</c> verbs.</summary>
    /// <param name="rules">The job's effective <c>allowed_tools</c>.</param>
    /// <returns>Whether it is a mail job.</returns>
    public static bool IsMailJob(IEnumerable<string> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return rules.Any(NamesOutlook);
    }

    // A shell rule (whole, or one piece of a comma list) whose program is zyggy and whose rest names outlook.
    private static bool NamesOutlook(string entry) =>
        entry.Split(',').Append(entry).Any(piece => LooseRule().Match(piece) is { Success: true } m
            && (m.Groups["tool"].Value.Equals("Bash", StringComparison.OrdinalIgnoreCase) || m.Groups["tool"].Value.Equals("PowerShell", StringComparison.OrdinalIgnoreCase))
            && m.Groups["spec"].Success
            && ShellNamesOutlook(m.Groups["spec"].Value));

    private static bool ShellNamesOutlook(string specifier)
    {
        // Environment prefixes (NAME=value) are skipped: the program is the first word without '='.
        var words = specifier.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var at = Array.FindIndex(words, w => !w.Contains('=', StringComparison.Ordinal));
        if (at < 0)
        {
            return false;
        }

        var name = words[at].Trim('"', '\'', '&').Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        return name.Contains("zyggy", StringComparison.OrdinalIgnoreCase)
            && words.Skip(at + 1).Any(w => w.Contains("outlook", StringComparison.OrdinalIgnoreCase));
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
