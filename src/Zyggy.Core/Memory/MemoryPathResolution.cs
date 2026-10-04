namespace Zyggy.Core.Memory;

/// <summary>The outcome of <see cref="MemoryPaths.TryResolve"/>: a full path inside the principal directory, or a refusal.</summary>
/// <param name="Succeeded">Whether the path was accepted.</param>
/// <param name="FullPath">The absolute path when accepted.</param>
/// <param name="RelativePath">The path relative to the principal directory with <c>/</c> separators, when accepted.</param>
/// <param name="Area">The area the path belongs to, when accepted.</param>
/// <param name="Refusal">Why the path was refused, when not accepted.</param>
public sealed record MemoryPathResolution(
    bool Succeeded,
    string? FullPath,
    string? RelativePath,
    MemoryArea Area,
    MemoryPathRefusal? Refusal)
{
    internal static MemoryPathResolution Accept(string fullPath, string relativePath, MemoryArea area) =>
        new(true, fullPath, relativePath, area, null);

    internal static MemoryPathResolution Refuse(MemoryPathRefusal refusal) => new(false, null, null, MemoryArea.Other, refusal);
}
