namespace Zyggy.Core.Processes;

/// <summary>The outcome of one <see cref="IProcessRunner.RunAsync"/> call.</summary>
/// <param name="ExitCode">The exit code; <see langword="null"/> when the process did not start or was killed on timeout.</param>
/// <param name="Stdout">Captured standard output, up to <see cref="ProcessSpec.MaxStdoutBytes"/>.</param>
/// <param name="Stderr">Captured standard error, up to <see cref="ProcessSpec.MaxStderrBytes"/>.</param>
/// <param name="TimedOut">The timeout elapsed and the process tree was killed.</param>
/// <param name="StartFailed">The program could not be started (not found, no permission).</param>
/// <param name="StdoutTruncated">Standard output exceeded its capture cap.</param>
/// <param name="Duration">Wall-clock time from start to exit.</param>
public sealed record ProcessResult(
    int? ExitCode,
    string Stdout,
    string Stderr,
    bool TimedOut,
    bool StartFailed,
    bool StdoutTruncated,
    TimeSpan Duration);
