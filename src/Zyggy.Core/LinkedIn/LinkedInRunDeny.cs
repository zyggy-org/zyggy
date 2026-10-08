namespace Zyggy.Core.LinkedIn;

/// <summary>
/// What every unattended model run denies (spec 36 AC-8): the linkedin server's tools and the <c>zyggy linkedin</c> verbs. Together with
/// the server's absence from the run's MCP configuration and its refusal under <c>ZYGGY_HOOKS=off</c>, the third of three defences.
/// </summary>
internal static class LinkedInRunDeny
{
    public static readonly IReadOnlyList<string> Rules = ["mcp__linkedin__*", "Bash(zyggy linkedin *)"];
}
