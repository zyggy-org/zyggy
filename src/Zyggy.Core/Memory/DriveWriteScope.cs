using System.Text.RegularExpressions;

namespace Zyggy.Core.Memory;

/// <summary>The kind of a OneDrive write. <see cref="ReplaceById"/> exists only so the scope can be shown to refuse it: no writer method produces it.</summary>
internal enum DriveWriteKind
{
    CreateFolder,
    CreateFile,
    ReplaceById,
}

/// <summary>A OneDrive write about to be sent: what <see cref="DriveWriteScope.Check"/> decides on.</summary>
internal sealed record DriveWriteRequest(DriveWriteKind Kind, string DriveId, string ParentId, string Name, string? ConflictBehaviour, long Size);

/// <summary>
/// The archive offload's write guard (spec 38 AC-6): a write is sent only to the resolved OneDrive drive, as a project folder directly
/// under the <c>Zyggy/archive</c> folder or a new <c>&lt;slug&gt;.&lt;ext&gt;</c> file directly inside such a folder, create-only
/// (<c>conflictBehavior=fail</c>) and within the simple-upload limit. Anything else is refused before the request, naming the rule.
/// </summary>
internal sealed partial class DriveWriteScope(string driveId, string rootFolderId, long simpleUploadLimit = DriveWriteScope.SimpleUploadLimit)
{
    /// <summary>Graph's limit for a single-request upload (250 MB); larger items would need an upload session, which does not exist here.</summary>
    public const long SimpleUploadLimit = 250_000_000;

    private readonly HashSet<string> _projectFolders = new(StringComparer.Ordinal);

    /// <summary>Records <paramref name="folderId"/> as a project folder: read with the root folder as parent, or just created under it.</summary>
    public void KnowProjectFolder(string folderId)
    {
        ArgumentNullException.ThrowIfNull(folderId);
        _projectFolders.Add(folderId);
    }

    /// <summary>The rule <paramref name="request"/> breaks, or <see langword="null"/> when it may be sent.</summary>
    public string? Check(DriveWriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.DriveId, driveId, StringComparison.Ordinal))
        {
            return "other_drive";
        }

        if (request.Kind is not (DriveWriteKind.CreateFolder or DriveWriteKind.CreateFile))
        {
            return "replace_form";
        }

        if (!string.Equals(request.ConflictBehaviour, "fail", StringComparison.Ordinal))
        {
            return "conflict_behaviour";
        }

        if (request.Size > simpleUploadLimit)
        {
            return "too_large";
        }

        if (request.Kind == DriveWriteKind.CreateFolder)
        {
            return !string.Equals(request.ParentId, rootFolderId, StringComparison.Ordinal) ? "folder_parent_not_root"
                : !Slug.TryParse(request.Name, out _) || request.Name.Contains('\n', StringComparison.Ordinal) ? "folder_name_not_slug"
                : null;
        }

        if (string.Equals(request.ParentId, rootFolderId, StringComparison.Ordinal) || !_projectFolders.Contains(request.ParentId))
        {
            return "file_parent_not_project_folder";
        }

        var name = ItemName().Match(request.Name);
        return !name.Success ? "file_name"
            : !ArchiveMediaTypeWire.IsItemExtension(name.Groups["ext"].Value) ? "extension"
            : null;
    }

    // <slug>.<ext>: the slug grammar, one dot, an alphanumeric extension; nothing else (no path, no control character, no trailing dot).
    [GeneratedRegex(@"\A[a-z0-9][a-z0-9-]{0,59}\.(?<ext>[A-Za-z0-9]{1,10})\z", RegexOptions.CultureInvariant)]
    private static partial Regex ItemName();
}
