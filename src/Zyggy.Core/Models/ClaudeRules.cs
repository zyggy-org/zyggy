namespace Zyggy.Core.Models;

/// <summary>Claude Code permission-rule path forms.</summary>
internal static class ClaudeRules
{
    /// <summary>
    /// An absolute path in a rule: <c>//</c> then the path from the filesystem root with <c>/</c> separators — <c>/opt/x</c> becomes
    /// <c>//opt/x</c>, never <c>///opt/x</c>.
    /// </summary>
    public static string Absolute(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return "//" + path.Replace('\\', '/').TrimStart('/');
    }
}
