namespace Zyggy.Core.Memory;

/// <summary>
/// The OneDrive paths of the archive offload (spec 38): <c>&lt;root&gt;/&lt;project&gt;</c> and <c>&lt;root&gt;/&lt;project&gt;/&lt;slug&gt;.&lt;ext&gt;</c>
/// under the configured <c>offload.root_folder</c>. The only builder of a drive path.
/// </summary>
internal sealed class DriveArchivePath
{
    public DriveArchivePath(string rootFolder)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootFolder);
        Root = rootFolder;
        Segments = rootFolder.Split('/');
    }

    /// <summary>Gets the root folder as configured.</summary>
    public string Root { get; }

    /// <summary>Gets the root folder's segments.</summary>
    public IReadOnlyList<string> Segments { get; }

    public string ProjectFolder(Slug project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return Root + "/" + project.Value;
    }

    public string Item(Slug project, Slug slug, string extension)
    {
        ArgumentNullException.ThrowIfNull(slug);
        ArgumentException.ThrowIfNullOrEmpty(extension);
        return ProjectFolder(project) + "/" + FileName(slug, extension);
    }

    /// <summary>The item's file name in its project folder: <c>&lt;slug&gt;.&lt;ext&gt;</c>.</summary>
    public static string FileName(Slug slug, string extension)
    {
        ArgumentNullException.ThrowIfNull(slug);
        return slug.Value + "." + extension;
    }
}
