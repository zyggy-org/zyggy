namespace Zyggy.Core.Tests.Infrastructure;

/// <summary>
/// A temporary folder tree for discovery tests under <c>&lt;temp&gt;/zyggy-unit/&lt;guid&gt;</c>: roots, projects (with or without
/// <c>.git/</c>), hidden folders, files and links; deleted on dispose.
/// </summary>
internal sealed class ScanTree : IDisposable
{
    public ScanTree()
    {
        Base = Path.Combine(Path.GetTempPath(), "zyggy-unit", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Base);
    }

    public string Base { get; }

    private bool _disposed;

    /// <summary>Creates (or returns) a root folder directly under the tree's base.</summary>
    public string Root(string name)
    {
        string path = Path.Combine(Base, name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Creates a project folder holding <c>.claude/</c>; with <paramref name="git"/> also an empty <c>.git/</c>.</summary>
    public string Project(string root, string folder, bool git = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string path = Path.Combine(root, folder);
        Directory.CreateDirectory(Path.Combine(path, ".claude"));
        if (git)
        {
            Directory.CreateDirectory(Path.Combine(path, ".git"));
        }

        return path;
    }

    /// <summary>Creates a plain folder (no <c>.claude/</c>).</summary>
    public string Folder(string root, string folder)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string path = Path.Combine(root, folder);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Writes a file at a path relative to <paramref name="under"/>, creating its folders.</summary>
    public string File(string under, string relative, string content = "")
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string path = Path.Combine(under, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Creates a directory symbolic link; <see langword="false"/> when the OS refuses (no privilege).</summary>
    public bool Link(string linkPath, string target)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            Directory.CreateSymbolicLink(linkPath, target);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>The path as the scanner writes it: full, <c>/</c> separators.</summary>
    public static string Published(string path) => Path.GetFullPath(path).Replace('\\', '/');

    public void Dispose()
    {
        _disposed = true;
        try
        {
            Directory.Delete(Base, recursive: true);
        }
        catch (IOException)
        {
            // best effort: a temp tree left behind is harmless
        }
        catch (UnauthorizedAccessException)
        {
            // best effort
        }
    }
}
