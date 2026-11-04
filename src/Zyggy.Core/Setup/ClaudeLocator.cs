using Zyggy.Core.Verbs;

namespace Zyggy.Core.Setup;

/// <summary>
/// Finds the native Claude Code executable for <c>claude.path</c> (spec 42 AC-7, plan 42 A3): <c>claude.exe</c> on the process <c>PATH</c>,
/// then <c>&lt;UserProfile&gt;/.local/bin/claude.exe</c> (the native installer's place). Only <c>claude.exe</c> — an npm <c>claude.cmd</c> shim is never used.
/// </summary>
internal static class ClaudeLocator
{
    private const string Program = "claude.exe";

    /// <summary>The absolute path with forward slashes, or <see langword="null"/> when neither place holds it.</summary>
    public static string? Find(IReadOnlyDictionary<string, string?> environment, string userProfile)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrEmpty(userProfile);
        // Windows names the variable "Path"; the CLI's environment keeps the process's spelling, so it is looked up ignoring case.
        string? processPath = environment.FirstOrDefault(kv => string.Equals(kv.Key, "PATH", StringComparison.OrdinalIgnoreCase)).Value;
        string? found = ProgramLocator.OnPath(Program, new Dictionary<string, string?> { ["PATH"] = processPath })
            ?? ProgramLocator.OnPath(Program, new Dictionary<string, string?> { ["PATH"] = Path.Join(userProfile, ".local", "bin") });
        return found is null ? null : Path.GetFullPath(found).Replace('\\', '/');
    }
}
