namespace Zyggy.Core.Memory;

/// <summary>
/// Where the script verbs read <c>secret-patterns.txt</c> (spec 33 Configuration): <c>ZYGGY_SECRET_PATTERNS</c>; else
/// <c>&lt;instance&gt;/../.claude/hooks/secret-patterns.txt</c>, the instance being <c>ZYGGY_INSTANCE_DIR</c>, else
/// <c>$CLAUDE_PROJECT_DIR/instance</c>.
/// </summary>
internal static class SecretPatternsLocation
{
    /// <summary>Returns the pattern file's path, or <see langword="null"/> when no variable names a location.</summary>
    public static string? Resolve(IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        string? Get(string key) => environment.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value) ? value : null;

        if (Get("ZYGGY_SECRET_PATTERNS") is { } explicitPath)
        {
            return explicitPath;
        }

        var instance = Get("ZYGGY_INSTANCE_DIR") ?? (Get("CLAUDE_PROJECT_DIR") is { } project ? Path.Join(project, "instance") : null);
        return instance is null ? null : Path.GetFullPath(Path.Join(instance, "..", ".claude", "hooks", "secret-patterns.txt"));
    }

    /// <summary>
    /// The dream's and Central pickup's rule (spec 28, spec 13 AC-16): <c>ZYGGY_SECRET_PATTERNS</c>, else derived from
    /// <c>ZYGGY_INSTANCE_DIR</c> only; the path of an existing file, or the one-line error naming the key (fail closed).
    /// </summary>
    public static (string? Path, string? Error) ResolveForInstance(IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        string? Get(string key) => environment.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value) ? value : null;

        var instance = Get("ZYGGY_INSTANCE_DIR");
        var patterns = Get("ZYGGY_SECRET_PATTERNS")
            ?? (instance is null ? null : Path.GetFullPath(Path.Join(instance, "..", ".claude", "hooks", "secret-patterns.txt")));
        if (patterns is null)
        {
            return (null, "ZYGGY_SECRET_PATTERNS is not set (and no ZYGGY_INSTANCE_DIR to derive it from)");
        }

        return File.Exists(patterns) ? (patterns, null) : (null, $"secret patterns {patterns} are missing (ZYGGY_SECRET_PATTERNS)");
    }
}
