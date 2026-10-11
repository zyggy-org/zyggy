using System.Globalization;

namespace Zyggy.Core.Memory;

/// <summary>
/// The sidecar of an archived item (spec 37 Contracts "Sidecar format"): <c>name</c>, <c>description</c>, <c>updated</c> in the
/// writer's usual places, then <c>project</c>, <c>media_type</c>, <c>size_bytes</c>, <c>sha256</c>, <c>archived</c>, <c>source_name</c>;
/// the body is the description. Readable by <see cref="MemoryFileReader"/>, but not a memory file: no aliases, no bullet lines.
/// </summary>
internal sealed record ArchiveSidecar(
    string Name,
    string Description,
    Slug Project,
    ArchiveMediaType MediaType,
    long SizeBytes,
    string Sha256,
    DateOnly Archived,
    string SourceName,
    DateOnly Updated)
{
    private static readonly string[] Keys = ["project", "media_type", "size_bytes", "sha256", "archived", "source_name"];

    private static readonly string[] OffloadKeys = ["offloaded", "drive_item_id", "drive_path", "drive_url", "drive_quickxor"];

    /// <summary>Gets where the item went once the offload moved it (spec 38); <see langword="null"/> while its bytes are in the archive.</summary>
    public ArchiveOffload? Offload { get; init; }

    /// <summary>The extension the item is stored with, without the dot.</summary>
    public string ItemExtension => ArchiveMediaTypeWire.Extension(MediaType);

    public MemoryFile ToMemoryFile()
    {
        var keys = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["project"] = Project.Value,
            ["media_type"] = ArchiveMediaTypeWire.ToWire(MediaType),
            ["size_bytes"] = SizeBytes.ToString(CultureInfo.InvariantCulture),
            ["sha256"] = Sha256,
            ["archived"] = Date(Archived),
            ["source_name"] = SourceName,
        };
        if (Offload is { } offload)
        {
            keys["offloaded"] = Date(offload.Offloaded);
            keys["drive_item_id"] = offload.DriveItemId;
            keys["drive_path"] = offload.DrivePath;
            if (offload.DriveUrl is not null)
            {
                keys["drive_url"] = offload.DriveUrl;
            }

            keys["drive_quickxor"] = offload.DriveQuickXor;
        }

        return new MemoryFile(Name, Description, [], Updated, keys, true, [Description]);
    }

    /// <summary>
    /// Reads a sidecar back; <see langword="null"/> when any key is missing or malformed. The offload part is all-or-none: any offload key
    /// present makes <c>offloaded</c>, <c>drive_item_id</c>, <c>drive_path</c> and <c>drive_quickxor</c> required (<c>drive_url</c> optional).
    /// </summary>
    public static ArchiveSidecar? TryParse(MemoryFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!file.HasFrontMatter || file.Name is null || file.Description is null || file.Updated is not { } updated
            || Keys.Any(k => !file.UnknownKeys.ContainsKey(k)))
        {
            return null;
        }

        var keys = file.UnknownKeys;
        if (!(Slug.TryParse(keys["project"], out var project)
               && ArchiveMediaTypeWire.TryFromWire(keys["media_type"], out var type)
               && long.TryParse(keys["size_bytes"], NumberStyles.None, CultureInfo.InvariantCulture, out var size)
               && DateOnly.TryParseExact(keys["archived"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var archived)))
        {
            return null;
        }

        var sidecar = new ArchiveSidecar(file.Name, file.Description, project, type, size, keys["sha256"], archived, keys["source_name"], updated);
        if (!OffloadKeys.Any(keys.ContainsKey))
        {
            return sidecar;
        }

        return keys.TryGetValue("offloaded", out var offloaded)
               && DateOnly.TryParseExact(offloaded, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var offloadedOn)
               && keys.TryGetValue("drive_item_id", out var itemId) && itemId.Length > 0
               && keys.TryGetValue("drive_path", out var drivePath) && drivePath.Length > 0
               && keys.TryGetValue("drive_quickxor", out var quickXor) && quickXor.Length > 0
            ? sidecar with { Offload = new ArchiveOffload(offloadedOn, itemId, drivePath, keys.GetValueOrDefault("drive_url"), quickXor) }
            : null;
    }

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
