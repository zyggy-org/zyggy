using System.Text;
using System.Text.RegularExpressions;

namespace Zyggy.Core.Jobs.Hook;

/// <summary>
/// One <c>allowed_tools</c> entry in Claude Code's permission-rule grammar: <c>Tool</c> or <c>Tool(specifier)</c> (spec 15).
/// A malformed entry parses to <see langword="null"/> and so allows nothing.
/// </summary>
/// <param name="Tool">The tool name, or an MCP server prefix <c>mcp__&lt;server&gt;</c>.</param>
/// <param name="Specifier">The text between the brackets; <see langword="null"/> for a bare rule.</param>
internal sealed partial record ToolRule(string Tool, string? Specifier)
{
    public static ToolRule? Parse(string text)
    {
        var match = RulePattern().Match(text);
        if (!match.Success)
        {
            return null;
        }

        return new ToolRule(match.Groups["tool"].Value, match.Groups["spec"].Success ? match.Groups["spec"].Value : null);
    }

    /// <summary>Whether this rule names <paramref name="tool"/>: the same name, or a bare <c>mcp__&lt;server&gt;</c> rule for one of its tools.</summary>
    public bool Names(string tool) =>
        string.Equals(Tool, tool, StringComparison.Ordinal)
        || (Specifier is null
            && Tool.StartsWith("mcp__", StringComparison.Ordinal)
            && Tool.IndexOf("__", 5, StringComparison.Ordinal) < 0
            && tool.StartsWith(Tool + "__", StringComparison.Ordinal));

    /// <summary>
    /// Whether a shell <paramref name="part"/> matches this rule's command pattern: <c>*</c> is any text, a trailing <c>:*</c>
    /// means "starts with". The caller hands over the parts of a simple command only (<see cref="ShellCommand.WhyNotSimple"/>);
    /// a command that is not simple is matched as a whole, and only by a bare rule or the pattern <c>*</c>.
    /// </summary>
    public bool MatchesCommand(string part)
    {
        if (Specifier is null || Specifier == "*")
        {
            return true;
        }

        if (Specifier.EndsWith(":*", StringComparison.Ordinal))
        {
            return part.StartsWith(Specifier[..^2], StringComparison.Ordinal);
        }

        var pattern = "^" + Regex.Escape(Specifier).Replace("\\*", ".*", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(part, pattern, RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// Whether a path relative to the run directory (forward slashes, empty for the directory itself) matches this rule's glob:
    /// <c>*</c> within a folder, <c>**</c> across folders, a trailing <c>/**</c> also covering the folder itself.
    /// </summary>
    public bool MatchesPath(string relative, bool ignoreCase)
    {
        if (Specifier is null)
        {
            return true;
        }

        var pattern = new StringBuilder("^");
        var glob = Specifier;
        var coversFolder = glob.EndsWith("/**", StringComparison.Ordinal);
        if (coversFolder)
        {
            glob = glob[..^3];
        }

        for (var i = 0; i < glob.Length; i++)
        {
            if (glob[i] == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                pattern.Append(".*");
                i++;
            }
            else if (glob[i] == '*')
            {
                pattern.Append("[^/]*");
            }
            else if (glob[i] == '?')
            {
                pattern.Append("[^/]");
            }
            else
            {
                pattern.Append(Regex.Escape(glob[i].ToString()));
            }
        }

        pattern.Append(coversFolder ? "(/.*)?$" : "$");
        var options = RegexOptions.Singleline | RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
        return Regex.IsMatch(relative, pattern.ToString(), options, TimeSpan.FromSeconds(1));
    }

    /// <summary>Whether a <c>WebFetch(domain:&lt;host&gt;)</c> rule covers <paramref name="url"/>'s host exactly.</summary>
    public bool MatchesUrl(string url)
    {
        if (Specifier is null)
        {
            return true;
        }

        const string Domain = "domain:";
        return Specifier.StartsWith(Domain, StringComparison.Ordinal)
            && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && string.Equals(uri.Host, Specifier[Domain.Length..], StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"^(?<tool>[A-Za-z0-9_\-]+)(\((?<spec>.*)\))?$", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex RulePattern();
}
