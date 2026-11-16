namespace Zyggy.Core.Registry;

/// <summary>
/// How a discovered project's registry name is derived (<c>node.json</c> <c>discovery.naming</c>, spec 45).
/// </summary>
public enum DiscoveryNaming
{
    /// <summary>The project folder's own name, folded (<c>My-Core-Audit</c> → <c>my-core-audit</c>).</summary>
    Leaf,

    /// <summary>
    /// The path below the innermost root the project was found from, folded and joined with <c>-</c> (<c>Common/devops</c> →
    /// <c>common-devops</c>); at level 1 the same as <see cref="Leaf"/>. The name then carries the folder segments below the root.
    /// </summary>
    Path,
}
