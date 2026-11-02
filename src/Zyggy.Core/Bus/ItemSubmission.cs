using Zyggy.Core.Envelope;
using Zyggy.Core.Memory;

namespace Zyggy.Core.Bus;

/// <summary>
/// What <see cref="ItemSubmitter.SubmitAsync"/> did, with what <c>zyggy archive</c>'s second output line prints (spec 41 AC-18):
/// the route, <c>&lt;project&gt;/&lt;drive_name&gt;</c>, the name, the media type and the size. The item fields are
/// <see langword="null"/> when the file was refused before its type was known.
/// </summary>
/// <param name="Result">The outcome.</param>
/// <param name="Route">The route taken, once one was chosen.</param>
/// <param name="ItemPath"><c>&lt;project&gt;/&lt;drive_name&gt;</c>.</param>
/// <param name="Name">The item's name.</param>
/// <param name="MediaType">The media type string (<c>text/markdown</c>, <c>image/png</c> …).</param>
/// <param name="Size">The file size in bytes; 0 when unknown.</param>
public sealed record ItemSubmission(SubmitResult Result, ItemRoute? Route, string? ItemPath, string? Name, string? MediaType, long Size)
{
    /// <summary>Gets <see cref="Size"/> as the archive's index line prints it (<c>2.0 KB</c>).</summary>
    public string SizeText => ArchiveSize.Format(Size);
}
