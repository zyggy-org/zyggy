namespace Zyggy.Core.Models;

/// <summary>What a model session is cut off from; flags combine.</summary>
[Flags]
public enum ModelSessionIsolation
{
    /// <summary>No isolation beyond the runtime defaults.</summary>
    None = 0,

    /// <summary>No MCP server: only explicitly given configuration, and every <c>mcp__*</c> tool disallowed.</summary>
    NoMcp = 1,

    /// <summary>No hooks (<c>disableAllHooks</c>).</summary>
    NoHooks = 2,

    /// <summary>No auto memory (<c>autoMemoryEnabled: false</c>).</summary>
    NoAutoMemory = 4,

    /// <summary>No slash commands or skills.</summary>
    NoSlashCommands = 8,
}
