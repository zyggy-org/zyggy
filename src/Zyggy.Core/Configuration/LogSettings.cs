using Microsoft.Extensions.Logging;

namespace Zyggy.Core.Configuration;

/// <summary>The <c>log</c> section of <c>node.json</c> (founding spec §11 as amended by A7-3): the minimum level; retention is fixed at 14 days.</summary>
/// <param name="Level">One of <c>Debug</c>, <c>Information</c>, <c>Warning</c>, <c>Error</c>; <c>Information</c> when absent.</param>
public sealed record LogSettings(string Level = "Information")
{
    /// <summary>The accepted <c>level</c> values, in order of verbosity.</summary>
    public static IReadOnlyList<string> Levels { get; } = ["Debug", "Information", "Warning", "Error"];

    /// <summary>The <see cref="LogLevel"/> of <see cref="Level"/>.</summary>
    /// <returns>The level.</returns>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="Level"/> is not one of <see cref="Levels"/> (the reader refuses such a file).</exception>
    public LogLevel ToLogLevel() => Level switch
    {
        "Debug" => LogLevel.Debug,
        "Information" => LogLevel.Information,
        "Warning" => LogLevel.Warning,
        "Error" => LogLevel.Error,
        _ => throw new InvalidOperationException("log.level must be Debug, Information, Warning or Error"),
    };
}
