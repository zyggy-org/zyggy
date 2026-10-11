using System.Text.Json;

namespace Zyggy.Core.M365.Graph;

/// <summary>
/// A drive item as the archive offload reads it (spec 38): id, name, parent, drive, size, OneDrive's content hash (files only) and its
/// <c>webUrl</c>; <see cref="IsFolder"/> for a folder or the drive root.
/// </summary>
internal sealed record DriveItemInfo(
    string Id, string Name, string? ParentId, string? DriveId, long Size, string? QuickXorHash, string? WebUrl, bool IsFolder)
{
    /// <summary>The <c>$select</c> every offload read asks for.</summary>
    public const string Select = "id,name,size,file,folder,root,parentReference,webUrl";

    /// <summary>The item in <paramref name="element"/>, or <see langword="null"/> when its id is missing or not an item id.</summary>
    public static DriveItemInfo? Parse(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || Text(element, "id") is not { } id || !M365Grammar.ItemId().IsMatch(id))
        {
            return null;
        }

        var parent = element.TryGetProperty("parentReference", out var p) ? p : default;
        var file = element.TryGetProperty("file", out var f) ? f : default;
        var hashes = file.ValueKind == JsonValueKind.Object && file.TryGetProperty("hashes", out var h) ? h : default;
        return new DriveItemInfo(
            id,
            Text(element, "name") ?? string.Empty,
            Text(parent, "id"),
            Text(parent, "driveId"),
            element.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number && size.TryGetInt64(out var n) ? n : 0,
            Text(hashes, "quickXorHash"),
            Text(element, "webUrl"),
            IsPresent(element, "folder") || IsPresent(element, "root"));
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool IsPresent(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
}
