namespace Zyggy.Core.Dream;

/// <summary>
/// One dream run at a time (AC-8): an exclusive OS lock on <c>&lt;state dir&gt;/dream.lock</c>, released by the OS when the holder
/// exits or is killed, so there is no stale-timestamp logic.
/// </summary>
internal sealed class DreamLock : IDisposable
{
    private readonly FileStream _stream;

    private DreamLock(FileStream stream) => _stream = stream;

    public static DreamLock? TryAcquire(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        try
        {
            return new DreamLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Dispose() => _stream.Dispose();
}
