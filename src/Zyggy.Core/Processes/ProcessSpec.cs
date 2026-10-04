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

    /// <summary>Text written to standard input as UTF-8 without a BOM, after which the stream is closed; <see langword="null"/> closes it at once.</summary>
    public string? StandardInput { get; init; }

    /// <summary>How long the process may run before its whole tree is killed; must be positive.</summary>
    public TimeSpan Timeout { get; init; }

    /// <summary>Called for every standard-output line, also for lines beyond <see cref="MaxStdoutBytes"/>.</summary>
    public Action<string>? OnStdoutLine { get; init; }

    /// <summary>Capture cap for standard output in bytes; lines beyond it are streamed but not retained.</summary>
    public int MaxStdoutBytes { get; init; } = 2 * 1024 * 1024;

    /// <summary>Capture cap for standard error in bytes.</summary>
    public int MaxStderrBytes { get; init; } = 64 * 1024;
}
