namespace Zyggy.Integration.Infrastructure;

/// <summary>A per-test directory under &lt;temp&gt;/zyggy-it/, deleted on dispose.</summary>
public sealed class ScratchDirectory : IDisposable
{
    /// <summary>Creates a fresh, empty directory.</summary>
    public ScratchDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zyggy-it", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    /// <summary>The directory's absolute path.</summary>
    public string Path { get; }

    /// <summary>Returns the path of <paramref name="name"/> inside the directory (the file is not created).</summary>
    public string File(string name) => System.IO.Path.Combine(Path, name);

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Leaked temp directories are documented in the README.
        }
    }
}
