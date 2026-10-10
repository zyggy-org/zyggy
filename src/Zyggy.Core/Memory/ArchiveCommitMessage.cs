using System.Globalization;

using Zyggy.Core.Dream;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Memory;

/// <summary>
/// The commits of <c>archive add</c> and <c>archive remove</c> (spec 37 AC-15, AC-19): subject, a body naming type, size and hash
/// (never the description), the trailer <c>Zyggy-Tool: memory archive</c>; and the two repository paths they touch.
/// </summary>
internal static class ArchiveCommitMessage
{
    private const string Trailer = "Zyggy-Tool: memory archive\n";

    public static string Add(Slug project, Slug slug, string extension, ArchiveMediaType type, long sizeBytes, string sha256)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(slug);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"archive add {project.Value}/{slug.Value}.{extension}\n\nmedia: {ArchiveMediaTypeWire.ToWire(type)}; size: {sizeBytes} bytes; sha256: {sha256}\n\n{Trailer}");
    }

    public static string Remove(Slug project, Slug slug, string extension)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(slug);
        return $"archive remove {project.Value}/{slug.Value}.{extension}\n\n{Trailer}";
    }

    /// <summary>The paths relative to the repository root (<c>&lt;tenant&gt;/&lt;user&gt;/&lt;relative&gt;</c>), ordinal order.</summary>
    public static IReadOnlyList<string> RepoPaths(Principal principal, params string[] relative) => DreamCommitter.RepoPaths(principal, relative);
}
