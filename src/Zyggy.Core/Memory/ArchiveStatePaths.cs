namespace Zyggy.Core.Memory;

/// <summary>
/// The archive offload's machine-local state (spec 38): <c>&lt;ZYGGY_STATE_DIR&gt;/archive</c> (default <c>~/.local/state/zyggy/archive</c>),
/// 0700 on Linux, holding the failure state file and the run lock. The only builder of these paths.
/// </summary>
internal sealed class ArchiveStatePaths
{
    public ArchiveStatePaths(IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        string? Get(string key) => environment.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value) ? value : null;

        var home = Get("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        StateDirectory = Path.Join(Get("ZYGGY_STATE_DIR") ?? Path.Join(home, ".local", "state", "zyggy"), "archive");
    }

    /// <summary>Gets the state directory.</summary>
    public string StateDirectory { get; }

    /// <summary>Gets the failure state file.</summary>
    public string StateFile => Path.Join(StateDirectory, "offload-state.json");

    /// <summary>Gets the run lock.</summary>
    public string LockFile => Path.Join(StateDirectory, "offload.lock");
}
