namespace Zyggy.Core.Memory;

/// <summary>
/// Where an archived item went (spec 38 sidecar): the offload date, the OneDrive item id, its drive path, its <c>webUrl</c> (absent when
/// Graph gave none) and the QuickXorHash the upload was verified against.
/// </summary>
internal sealed record ArchiveOffload(DateOnly Offloaded, string DriveItemId, string DrivePath, string? DriveUrl, string DriveQuickXor);
