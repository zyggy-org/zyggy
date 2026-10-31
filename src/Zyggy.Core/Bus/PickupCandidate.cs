using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Bus;

/// <summary>
/// A file another machine of the tenant left in its own <c>context/</c> or <c>reports/</c> folder, read once for Central's pickup
/// (spec 13, founding spec §7 step 1 as amended by A13-3/A13-10). Nothing about it is trusted until it is verified.
/// </summary>
/// <param name="File">The repository-relative path and its file-name ULID (synthetic when the name is not one).</param>
/// <param name="Folder">The machine whose folder holds the file.</param>
/// <param name="Kind"><see cref="BusFolder.Context"/> or <see cref="BusFolder.Reports"/>.</param>
/// <param name="Bytes">The file's bytes, read under the checkout write lock.</param>
public sealed record PickupCandidate(BusFile File, MachineName Folder, BusFolder Kind, byte[] Bytes);
