namespace Zyggy.Core.Memory;

/// <summary>
/// One archive offload at a time (spec 38 CLI): an exclusive OS lock on <c>&lt;state&gt;/archive/offload.lock</c>, released by the OS when
/// the holder exits or is killed — the <c>DreamLock</c> pattern, no stale-timestamp logic.
/// </summary>
internal sealed class OffloadLock : IDisposable
{
    private readonly FileStream _stream;

    private OffloadLock(FileStream stream) => _stream = stream;

    /// <summary>The lock, or <see langword="null"/> when another run holds it.</summary>
    public static OffloadLock? TryAcquire(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        try
        {
            return new OffloadLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Dispose() => _stream.Dispose();
}
