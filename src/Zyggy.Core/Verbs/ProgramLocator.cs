namespace Zyggy.Core.Verbs;

/// <summary>Finds an installed program the way the template scripts did (<c>command -v</c>, then <c>zy_m365_user_bin</c>).</summary>
internal static class ProgramLocator
{
    /// <summary><c>command -v &lt;name&gt;</c>: the first executable <paramref name="name"/> on <c>PATH</c>.</summary>
    public static string? OnPath(string name, IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var path = environment.TryGetValue("PATH", out var value) ? value : null;
        foreach (var directory in (path ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Join(directory, name);
            if (IsExecutable(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// <c>zy_m365_user_bin</c>: a user-installed program (npm prefix <c>~/.local</c>, pipx) — <c>PATH</c> first, then
    /// <c>$HOME/.local/bin</c>, which a systemd unit's <c>PATH</c> does not carry.
    /// </summary>
    public static string? UserProgram(string name, IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (OnPath(name, environment) is { } found)
        {
            return found;
        }

        var home = environment.TryGetValue("HOME", out var value) && !string.IsNullOrEmpty(value)
            ? value
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidate = Path.Join(home, ".local", "bin", name);
        return IsExecutable(candidate) ? candidate : null;
    }

    private static bool IsExecutable(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        const UnixFileMode Execute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        return (File.GetUnixFileMode(path) & Execute) != 0;
    }
}
