using System.Globalization;
using System.Text;

using Zyggy.Core.Envelope;

namespace Zyggy.Core.Jobs;

/// <summary>
/// One run per project (founding spec §6 step 3 as amended by A1): an exclusive OS handle on <c>&lt;project&gt;/.claude/zyggy.lock</c>
/// (<see cref="FileShare.None"/>, <c>flock</c> on Linux) held for the whole run and released by the OS when the holder exits or dies, so
/// there is no age-based staleness rule (ex-Q1). The <c>job</c>/<c>pid</c>/<c>acquired</c> lines are for diagnostics only. The file is
/// deleted when released, like <c>BusCheckoutLock</c>.
/// </summary>
internal sealed class ProjectLock : IDisposable
{
    private readonly FileStream _stream;

    private ProjectLock(FileStream stream, string path)
    {
        _stream = stream;
        Path = path;
    }

    /// <summary>Gets the process id written into the lock: the one place that reads it (plan 06 A4).</summary>
    public static int CurrentProcessId => Environment.ProcessId;

    /// <summary>Gets the lock file's path.</summary>
    public string Path { get; }

    /// <summary>The lock file of <paramref name="projectPath"/>.</summary>
    public static string PathOf(string projectPath) => System.IO.Path.Join(projectPath, ".claude", "zyggy.lock");

    /// <summary>Takes the lock and writes its three lines, or returns <see langword="null"/> when another handle holds it.</summary>
    public static ProjectLock? TryAcquire(string projectPath, EnvelopeId jobId, int pid, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentNullException.ThrowIfNull(jobId);
        var path = PathOf(projectPath);
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            // Windows reports a file pending deletion by the previous holder this way.
            return null;
        }

        var content = string.Create(
            CultureInfo.InvariantCulture,
            $"job: {jobId.Value}\npid: {pid}\nacquired: {now.ToUniversalTime():yyyy-MM-dd'T'HH:mm:ss'Z'}\n");
        stream.SetLength(0);
        stream.Write(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content));
        stream.Flush(flushToDisk: true);
        return new ProjectLock(stream, path);
    }

    /// <summary>Whether another handle holds the lock of <paramref name="projectPath"/>; creates nothing (07's pre-claim check).</summary>
    public static bool IsHeld(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        try
        {
            using var probe = new FileStream(PathOf(projectPath), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>The lock file's content, read through the held handle (the file cannot be opened a second time while held).</summary>
    public string ReadContent()
    {
        _stream.Position = 0;
        var bytes = new byte[_stream.Length];
        _stream.ReadExactly(bytes);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Releases the handle; the OS deletes the file (best effort).</summary>
    public void Dispose() => _stream.Dispose();
}
