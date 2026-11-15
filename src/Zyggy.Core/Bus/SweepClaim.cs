using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Bus;

/// <summary>
/// A file in a machine's <c>jobs/claimed/</c> folder, read once for Central's sweep (spec 17). Nothing about it is trusted until it
/// is verified.
/// </summary>
/// <param name="File">The repository-relative path and its file-name ULID (synthetic when the name is not one).</param>
/// <param name="Folder">The machine whose folder holds the file.</param>
/// <param name="Bytes">The file's bytes, read under the checkout write lock.</param>
/// <param name="ClaimTime">The committer time of the newest commit that touched the path; <see langword="null"/> when git could not say.</param>
internal sealed record SweepClaim(BusFile File, MachineName Folder, byte[] Bytes, DateTimeOffset? ClaimTime);
