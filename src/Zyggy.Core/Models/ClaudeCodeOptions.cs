namespace Zyggy.Core.Models;

/// <summary>Configuration of the Claude Code CLI model runner.</summary>
public sealed class ClaudeCodeOptions
{
    /// <summary>The <c>claude</c> executable: an absolute path, or a name resolved on <c>PATH</c> (hosts bind <c>ZYGGY_CLAUDE_PATH</c>).</summary>
    public string Path { get; set; } = "claude";
}
