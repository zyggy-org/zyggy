namespace Zyggy.Core.Configuration;

/// <summary>
/// The <c>archive</c> section of a laptop's <c>node.json</c> (founding spec §10, W41-15): the laptop cap for <c>zyggy archive</c> and the
/// locally synced OneDrive archive folder, when this laptop has one. Never a secret.
/// </summary>
public sealed record ArchiveSettings
{
    /// <summary>The largest <see cref="ItemMaxBytes"/> a laptop may set: 25 MiB, Central's item ceiling.</summary>
    public const long ItemCeiling = 26_214_400;

    /// <summary>Gets <c>archive.itemMaxBytes</c>: the largest file <c>zyggy archive</c> sends, 1 … <see cref="ItemCeiling"/>; 10 MiB when absent.</summary>
    public long ItemMaxBytes { get; init; } = 10_485_760;

    /// <summary>
    /// Gets <c>archive.oneDriveFolder</c>: the absolute path of the locally synced copy of Central's <c>offload.root_folder</c>;
    /// <see langword="null"/> (absent or <c>null</c>) means this laptop has no OneDrive access and items take the bus route.
    /// </summary>
    public string? OneDriveFolder { get; init; }
}
