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

    /// <summary>The extension the item is stored with, without the dot.</summary>
    public string ItemExtension => ArchiveMediaTypeWire.Extension(MediaType);

    public MemoryFile ToMemoryFile() => new(
        Name,
        Description,
        [],
        Updated,
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["project"] = Project.Value,
            ["media_type"] = ArchiveMediaTypeWire.ToWire(MediaType),
            ["size_bytes"] = SizeBytes.ToString(CultureInfo.InvariantCulture),
            ["sha256"] = Sha256,
            ["archived"] = Date(Archived),
            ["source_name"] = SourceName,
        },
        true,
        [Description]);

    /// <summary>Reads a sidecar back; <see langword="null"/> when any key is missing or malformed.</summary>
    public static ArchiveSidecar? TryParse(MemoryFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!file.HasFrontMatter || file.Name is null || file.Description is null || file.Updated is not { } updated
            || Keys.Any(k => !file.UnknownKeys.ContainsKey(k)))
        {
            return null;
        }

        var keys = file.UnknownKeys;
        return Slug.TryParse(keys["project"], out var project)
               && ArchiveMediaTypeWire.TryFromWire(keys["media_type"], out var type)
               && long.TryParse(keys["size_bytes"], NumberStyles.None, CultureInfo.InvariantCulture, out var size)
               && DateOnly.TryParseExact(keys["archived"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var archived)
            ? new ArchiveSidecar(file.Name, file.Description, project, type, size, keys["sha256"], archived, keys["source_name"], updated)
            : null;
    }

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
