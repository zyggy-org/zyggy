using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Memory.Mirror;

/// <summary>
/// Every path inside a mirror repository (spec 47): the fixed read-me at the root and the memory layout
/// <c>&lt;tenant&gt;/&lt;user&gt;/&lt;path&gt;</c>, always with <c>/</c> separators. The only place mirror paths are built.
/// </summary>
internal static class MirrorPaths
{
    /// <summary>The read-me at the mirror's root.</summary>
    public const string ReadMe = "MIRROR.md";

    /// <summary>Areas never copied, whatever a reach says (relative to <c>&lt;tenant&gt;/&lt;user&gt;/</c>).</summary>
    public static readonly IReadOnlyList<string> NeverCopied = ["inbox/", "auto/", ".dream/"];

    /// <summary>The archive folder, under which only sidecars are copied.</summary>
    public const string Archive = "archive/";

    /// <summary>Returns the mirror path of a memory file.</summary>
    /// <param name="principal">Whose memory.</param>
    /// <param name="relativePath">The path relative to the principal directory, <c>/</c> separators.</param>
    /// <returns><c>&lt;tenant&gt;/&lt;user&gt;/&lt;relativePath&gt;</c>.</returns>
    public static string File(Principal principal, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentException.ThrowIfNullOrEmpty(relativePath);
        return $"{principal.Tenant.Value}/{principal.User.Value}/{relativePath}";
    }
}
