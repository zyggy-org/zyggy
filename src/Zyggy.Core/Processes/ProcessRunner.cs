using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Zyggy.Core.Processes;

/// <summary>
/// The real <see cref="IProcessRunner"/>: starts the program directly (argument list, never a shell), writes standard input
/// on its own task, streams standard output line by line, and kills the whole process tree on timeout or cancellation.
/// </summary>
internal sealed class ProcessRunner(TimeProvider clock) : IProcessRunner
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // After the process exits (or is killed) its pipes close; a grandchild that escaped the kill could keep them open.
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(spec.Timeout, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();

        var started = clock.GetTimestamp();
        using var process = new Process { StartInfo = StartInfo(spec) };
        try
        {
            if (!process.Start())
            {
                return StartFailed(clock.GetElapsedTime(started));
            }
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or InvalidOperationException or DirectoryNotFoundException)
        {
            return StartFailed(clock.GetElapsedTime(started));
        }

        var stdin = WriteStdinAsync(process, spec.StandardInput);
        var stdout = new BoundedCapture(spec.MaxStdoutBytes);
        var stdoutTask = ReadStdoutAsync(process.StandardOutput, stdout, spec.OnStdoutLine);
        var stderr = new BoundedCapture(spec.MaxStderrBytes);
        var stderrTask = ReadStderrAsync(process.StandardError, stderr);

        var timedOut = false;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(spec.Timeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                KillTree(process);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                await DrainAsync(stdin, stdoutTask, stderrTask).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                timedOut = true;
            }
        }

        await DrainAsync(stdin, stdoutTask, stderrTask).ConfigureAwait(false);
        return new ProcessResult(
            timedOut ? null : process.ExitCode,
            stdout.Text,
            stderr.Text,
            timedOut,
            StartFailed: false,
            stdout.Truncated,
            clock.GetElapsedTime(started));
    }

    private static ProcessStartInfo StartInfo(ProcessSpec spec)
    {
        var info = new ProcessStartInfo(spec.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = spec.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
        };
        foreach (var argument in spec.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in spec.Environment)
        {
            if (value is null)
            {
                info.Environment.Remove(name);
            }
            else
            {
                info.Environment[name] = value;
            }
        }

        return info;
    }

    private static ProcessResult StartFailed(TimeSpan duration) =>
        new(null, string.Empty, string.Empty, TimedOut: false, StartFailed: true, StdoutTruncated: false, duration);

    private static async Task WriteStdinAsync(Process process, string? text)
    {
        try
        {
            if (text is not null)
            {
                var bytes = Utf8NoBom.GetBytes(text);
                await process.StandardInput.BaseStream.WriteAsync(bytes).ConfigureAwait(false);
                await process.StandardInput.BaseStream.FlushAsync().ConfigureAwait(false);
            }

            process.StandardInput.Close();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The child exited without reading all of its input (broken pipe).
        }
    }

    private static async Task ReadStdoutAsync(StreamReader reader, BoundedCapture capture, Action<string>? onLine)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            capture.AppendLine(line);
            onLine?.Invoke(line);
        }
    }

    private static async Task ReadStderrAsync(StreamReader reader, BoundedCapture capture)
    {
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            capture.Append(buffer.AsSpan(0, read));
        }
    }

    private static async Task DrainAsync(Task stdin, Task stdout, Task stderr)
    {
        try
        {
            await Task.WhenAll(stdin, stdout, stderr).WaitAsync(DrainTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A pipe stayed open after the process ended; what was captured so far is the result.
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already exited.
        }
    }

    /// <summary>Keeps text up to a byte cap and records whether more arrived.</summary>
    private sealed class BoundedCapture(int maxBytes)
    {
        private readonly StringBuilder _text = new();
        private readonly Lock _gate = new();
        private int _bytes;

        public bool Truncated { get; private set; }

        public string Text
        {
            get
            {
                lock (_gate)
                {
                    return _text.ToString();
                }
            }
        }

        public void AppendLine(string line) => Append((line + "\n").AsSpan());

        public void Append(ReadOnlySpan<char> chunk)
        {
            lock (_gate)
            {
                if (Truncated)
                {
                    return;
                }

                var size = Utf8NoBom.GetByteCount(chunk);
                if (_bytes + size > maxBytes)
                {
                    Truncated = true;
                    return;
                }

                _bytes += size;
                _text.Append(chunk);
            }
        }
    }
}
