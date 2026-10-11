using System.Globalization;

namespace Zyggy.Core.Memory;

/// <summary>
/// The commit of one offloaded item (spec 38 AC-14): subject <c>archive offload &lt;project&gt;/&lt;slug&gt;.&lt;ext&gt;</c> (its <c>archive </c>
/// prefix lets 37's preflight push a deferred one first), a body naming the drive item, both hashes and the size, the trailer
/// <c>Zyggy-Tool: memory archive offload</c>.
/// </summary>
internal static class OffloadCommitMessage
{
    public static string For(Slug project, Slug slug, string extension, string driveItemId, string sha256, string quickXor, long size)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(slug);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"archive offload {project.Value}/{slug.Value}.{extension}\n\ndrive_item_id: {driveItemId}; sha256: {sha256}; quickxor: {quickXor}; size: {size} bytes\n\nZyggy-Tool: memory archive offload\n");
    }
}
