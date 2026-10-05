namespace Zyggy.Core.Verbs;

/// <summary>Path resolution as the template scripts did it with <c>realpath</c> / <c>readlink -f</c>.</summary>
internal static class PathResolution
{
    /// <summary><c>realpath -m</c>: every existing component's symbolic links followed, missing components kept as written.</summary>
    public static string RealPath(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)!;
        var current = root;
        foreach (var segment in fullPath[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Join(current, segment);
            var info = new FileInfo(current);
            if (info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                current = RealPath(target.FullName);
            }
        }

        return current;
    }
}
