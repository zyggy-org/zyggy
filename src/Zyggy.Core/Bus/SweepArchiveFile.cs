using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Bus;

/// <summary>
/// A file in a machine's <c>reports/</c> or <c>context/</c> folder, read once for Central's sweep archive step (spec 17). Nothing
/// about it is trusted until it is verified.
/// </summary>
/// <param name="File">The repository-relative path and its file-name ULID (synthetic when the name is not one).</param>
/// <param name="Folder">The machine whose folder holds the file.</param>
/// <param name="Kind"><see cref="BusFolder.Reports"/> or <see cref="BusFolder.Context"/>; <see cref="BusFolder.Decisions"/> for a file of Central's decisions folder (deliverable 18).</param>
/// <param name="Bytes">The file's bytes, read under the checkout write lock.</param>
/// <param name="Created">The header's <c>created</c>, read leniently; <see langword="null"/> when it cannot be read.</param>
/// <param name="AddedTime">For a file without a readable <c>created</c>: the committer time of the commit that added it; otherwise, or when git could not say, <see langword="null"/>.</param>
internal sealed record SweepArchiveFile(BusFile File, MachineName Folder, BusFolder Kind, byte[] Bytes, DateTimeOffset? Created, DateTimeOffset? AddedTime)
{
    /// <summary>Gets the instant the file's age and archive month come from: <c>created</c>, else the adding commit's time.</summary>
    public DateTimeOffset? Dated => Created ?? AddedTime;
}
