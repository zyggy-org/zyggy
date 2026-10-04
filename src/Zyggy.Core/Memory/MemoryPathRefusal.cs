namespace Zyggy.Core.Memory;

/// <summary>Why <see cref="MemoryPaths.TryResolve"/> refused a relative path.</summary>
public enum MemoryPathRefusal
{
    /// <summary>The path is rooted (absolute, a drive or a UNC path).</summary>
    Absolute,

    /// <summary>The path climbs out with <c>..</c>.</summary>
    Traversal,

    /// <summary>A segment is empty, <c>.</c>, contains a forbidden character, or breaks the layout's naming rules.</summary>
    InvalidSegment,

    /// <summary>The path names another principal's tree.</summary>
    OutsidePrincipal,

    /// <summary>A symbolic link on the way leads outside the principal directory.</summary>
    SymlinkEscape,
}
