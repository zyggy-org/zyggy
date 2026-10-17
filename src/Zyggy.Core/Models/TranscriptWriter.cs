using System.Text;

namespace Zyggy.Core.Models;

/// <summary>
/// Appends every stdout line of a model run to its transcript as it arrives (spec 06): UTF-8 without BOM, <c>\n</c> endings, so a
/// killed run keeps what it produced. The first open or write failure is reported once through <see cref="Failure"/> and stops
/// further writes; the run itself is never affected.
/// </summary>
internal sealed class TranscriptWriter : IDisposable
{
    private StreamWriter? _writer;

    private TranscriptWriter(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { NewLine = "\n", AutoFlush = true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Failure = $"{ex.GetType().Name} at {path}";
        }
    }

    /// <summary>Gets the failure (exception type and path, never a line of the stream), when the transcript could not be written.</summary>
    public string? Failure { get; private set; }

    public static TranscriptWriter? Open(string? path) => path is null ? null : new TranscriptWriter(path);

    public void Write(string line)
    {
        if (_writer is null)
        {
            return;
        }

        try
        {
            _writer.Write(line);
            _writer.Write('\n');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Failure = $"{ex.GetType().Name} while writing";
            Close();
        }
    }

    public void Dispose() => Close();

    private void Close()
    {
        var writer = _writer;
        _writer = null;
        try
        {
            writer?.Dispose();
        }
        catch (IOException ex)
        {
            Failure ??= $"{ex.GetType().Name} while closing";
        }
    }
}
