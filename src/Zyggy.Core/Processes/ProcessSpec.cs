using System.Collections.ObjectModel;

namespace Zyggy.Core.Processes;

/// <summary>What <see cref="IProcessRunner"/> starts: a program, its argument list and working directory (never a shell).</summary>
/// <param name="FileName">The program, an absolute path or a name resolved on <c>PATH</c>.</param>
/// <param name="Arguments">The argument vector, passed element by element.</param>
/// <param name="WorkingDirectory">The process working directory.</param>
public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory)
{
    /// <summary>Environment additions on top of the inherited environment; a <see langword="null"/> value removes the variable.</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = ReadOnlyDictionary<string, string?>.Empty;

    /// <summary>
    /// Gets a value indicating whether the child's environment is exactly <see cref="Environment"/> (<see langword="null"/> values ignored)
    /// instead of the inherited one plus additions. Default <see langword="false"/>: the 28 behaviour.
    /// </summary>
    public bool ReplaceEnvironment { get; init; }

    /// <summary>Text written to standard input as UTF-8 without a BOM, after which the stream is closed; <see langword="null"/> closes it at once.</summary>
    public string? StandardInput { get; init; }

    /// <summary>How long the process may run before its whole tree is killed; must be positive.</summary>
    public TimeSpan Timeout { get; init; }

    /// <summary>Called for every standard-output line, also for lines beyond <see cref="MaxStdoutBytes"/>.</summary>
    public Action<string>? OnStdoutLine { get; init; }

    /// <summary>Capture cap for standard output in bytes; lines beyond it are streamed but not retained.</summary>
    public int MaxStdoutBytes { get; init; } = 2 * 1024 * 1024;

    /// <summary>
    /// Gets the file that receives standard output byte for byte (spec 59: a fetched file, never re-encoded or split into lines);
    /// <see langword="null"/> captures it as text. When set, <see cref="ProcessResult.Stdout"/> is empty, <see cref="OnStdoutLine"/> and
    /// <see cref="MaxStdoutBytes"/> do not apply, and the file is created or overwritten.
    /// </summary>
    public string? StandardOutputPath { get; init; }

    /// <summary>Capture cap for standard error in bytes.</summary>
    public int MaxStderrBytes { get; init; } = 64 * 1024;

    /// <summary>
    /// Spec 61 slice 8: called once with the started process (pid, name, start time) right after it started, before any output is read;
    /// the node records its model processes through it. Internal: no new public surface. A callback that throws is ignored.
    /// </summary>
    internal Action<ProcessFacts>? OnStarted { get; init; }
}
