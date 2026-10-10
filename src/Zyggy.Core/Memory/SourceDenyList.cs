namespace Zyggy.Core.Memory;

/// <summary>
/// The closed list of locations <c>archive add</c> never reads from (spec 37 AC-7): credential and state folders, the memory repository
/// itself, plus the instance's <c>source_deny</c>. A path is covered when it lies under an entry (prefix on whole path components).
/// </summary>
internal sealed class SourceDenyList
{
    private readonly List<(string Directory, string Label)> _entries;

    private readonly StringComparison _comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private SourceDenyList(List<(string Directory, string Label)> entries) => _entries = entries;

    public static SourceDenyList Build(IReadOnlyDictionary<string, string?> environment, ArchiveOptions options, MemoryPaths paths)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(paths);
        string? Get(string key) => environment.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value) ? value : null;

        var home = Get("HOME") ?? Get("USERPROFILE") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var entries = new List<(string, string)>
        {
            (Path.Join(home, ".ssh"), "denied_location"),
            (Path.Join(home, ".config", "zyggy"), "denied_location"),
            (Path.Join(home, ".local", "state", "zyggy"), "denied_location"),
            (Path.Join(home, ".claude"), "denied_location"),
            (paths.RootDirectory, "inside_memory"),
        };
        if (Get("XDG_CONFIG_HOME") is { } xdg)
        {
            entries.Add((Path.Join(xdg, "zyggy"), "denied_location"));
        }

        if (Get("CREDENTIALS_DIRECTORY") is { } credentials)
        {
            entries.Add((credentials, "denied_location"));
        }

        entries.AddRange(options.SourceDeny.Select(entry => (entry, "denied_location")));
        return new SourceDenyList(entries.Select(e => (Normalise(e.Item1), e.Item2)).ToList());
    }

    /// <summary>Returns <c>inside_memory</c> or <c>denied_location</c> when <paramref name="fullPath"/> lies under an entry; else <see langword="null"/>.</summary>
    public string? Covers(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        var candidate = Normalise(fullPath);
        foreach (var (directory, label) in _entries)
        {
            if (candidate.StartsWith(directory + Path.DirectorySeparatorChar, _comparison) || string.Equals(candidate, directory, _comparison))
            {
                return label;
            }
        }

        return null;
    }

    private static string Normalise(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
