using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Setup;

/// <summary>
/// Every path <c>zyggy setup</c> reads or writes (plan 42 A5), built in one place: the three directories resolved from the options and the
/// environment, the <c>zyggy-core</c> layout, the user-scope Claude Code files, backups and temp files. No bus path is ever built here.
/// </summary>
public static class SetupPaths
{
    /// <summary>The variable Claude Code reads its user-scope directory from.</summary>
    public const string ClaudeConfigDirVariable = "CLAUDE_CONFIG_DIR";

    /// <summary>The user-scope Claude Code directory: <paramref name="option"/>, else <c>CLAUDE_CONFIG_DIR</c>, else <c>&lt;UserProfile&gt;/.claude</c> (spec 42 AC-16).</summary>
    /// <param name="option">The <c>--claude-dir</c> value, when given.</param>
    /// <param name="environment">The process environment; an empty value counts as unset.</param>
    /// <returns>The directory.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="environment"/> is null.</exception>
    public static string ResolveClaudeDir(string? option, IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (!string.IsNullOrEmpty(option))
        {
            return option;
        }

        return Variable(environment, ClaudeConfigDirVariable) ?? Path.Join(ResolveUserProfile(environment), ".claude");
    }

    /// <summary>The user's profile directory: <c>USERPROFILE</c>, else <c>HOME</c>, else the OS's user profile folder.</summary>
    /// <param name="environment">The process environment; an empty value counts as unset.</param>
    /// <returns>The directory.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="environment"/> is null.</exception>
    public static string ResolveUserProfile(IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return Variable(environment, "USERPROFILE") ?? Variable(environment, "HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    /// <summary>The local application data directory: <c>LOCALAPPDATA</c> (read on every OS, plan 42 R7), else the OS's folder.</summary>
    /// <param name="environment">The process environment; an empty value counts as unset.</param>
    /// <returns>The directory.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="environment"/> is null.</exception>
    public static string ResolveLocalAppData(IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return Variable(environment, "LOCALAPPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    }

    /// <summary>The default <c>--core-path</c>: <c>C:/agents/zyggy-core</c> on Windows, <c>&lt;UserProfile&gt;/agents/zyggy-core</c> elsewhere.</summary>
    /// <param name="environment">The process environment (for the user profile off Windows).</param>
    /// <returns>The path.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="environment"/> is null.</exception>
    public static string DefaultCorePath(IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return DefaultCorePath(OperatingSystem.IsWindows(), OperatingSystem.IsWindows() ? string.Empty : ResolveUserProfile(environment));
    }

    /// <summary>The per-run backup of <paramref name="file"/>: <c>&lt;file&gt;.bak-&lt;stamp&gt;</c>, beside it.</summary>
    /// <param name="file">The file about to change.</param>
    /// <param name="stamp">The run's <see cref="SetupRequest.BackupStamp"/>.</param>
    /// <returns>The backup path.</returns>
    public static string Backup(string file, string stamp) => file + ".bak-" + stamp;

    /// <summary>The temp file a new <paramref name="file"/> is written to before it replaces the original: <c>&lt;file&gt;.tmp-&lt;stamp&gt;</c>, beside it.</summary>
    /// <param name="file">The file about to change.</param>
    /// <param name="stamp">The run's <see cref="SetupRequest.BackupStamp"/>.</param>
    /// <returns>The temp path.</returns>
    public static string Temp(string file, string stamp) => file + ".tmp-" + stamp;

    /// <summary>The secret-pattern file of the checkout, with forward slashes (the value written into <c>env.ZYGGY_SECRET_PATTERNS</c>).</summary>
    /// <param name="corePath">The <c>zyggy-core</c> checkout.</param>
    /// <returns><c>&lt;core&gt;/.claude/hooks/secret-patterns.txt</c>.</returns>
    public static string SecretPatterns(string corePath) => Path.Join(corePath, ".claude", "hooks", "secret-patterns.txt").Replace('\\', '/');

    /// <summary>A node skill in the checkout.</summary>
    /// <param name="corePath">The <c>zyggy-core</c> checkout.</param>
    /// <param name="name">The skill's name.</param>
    /// <returns><c>&lt;core&gt;/node/skills/&lt;name&gt;</c>.</returns>
    public static string SkillSource(string corePath, string name) => Path.Join(corePath, "node", "skills", name);

    /// <summary>A user-scope skill.</summary>
    /// <param name="claudeDir">The user-scope Claude Code directory.</param>
    /// <param name="name">The skill's name.</param>
    /// <returns><c>&lt;claude-dir&gt;/skills/&lt;name&gt;</c>.</returns>
    public static string SkillTarget(string claudeDir, string name) => Path.Join(claudeDir, "skills", name);

    /// <summary>The user-scope Claude Code settings.</summary>
    /// <param name="claudeDir">The user-scope Claude Code directory.</param>
    /// <returns><c>&lt;claude-dir&gt;/settings.json</c>.</returns>
    public static string SettingsFile(string claudeDir) => Path.Join(claudeDir, "settings.json");

    /// <summary>Where a replaced skill is moved, outside the skills directory so Claude Code never loads it twice (spec 42 AC-17).</summary>
    /// <param name="localAppData">The local application data directory.</param>
    /// <param name="stamp">The run's <see cref="SetupRequest.BackupStamp"/>.</param>
    /// <param name="name">The skill's name.</param>
    /// <returns><c>&lt;LocalAppData&gt;/zyggy/backup/&lt;stamp&gt;/skills/&lt;name&gt;</c>.</returns>
    public static string SkillBackup(string localAppData, string stamp, string name) => Path.Join(localAppData, "zyggy", "backup", stamp, "skills", name);

    /// <summary>The spike file store's default folder for <paramref name="tenant"/> (checked for existence only; nothing inside is read).</summary>
    /// <param name="localAppData">The local application data directory.</param>
    /// <param name="tenant">The tenant.</param>
    /// <returns><c>&lt;LocalAppData&gt;/zyggy/secrets/&lt;tenant&gt;</c>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="tenant"/> is null.</exception>
    public static string SpikeStore(string localAppData, TenantId tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        return Path.Join(localAppData, "zyggy", "secrets", tenant.Value);
    }

    /// <summary>The default core path for an OS, testable on any OS.</summary>
    internal static string DefaultCorePath(bool windows, string userProfile) =>
        windows ? "C:/agents/zyggy-core" : Path.Join(userProfile, "agents", "zyggy-core");

    private static string? Variable(IReadOnlyDictionary<string, string?> environment, string name) =>
        environment.TryGetValue(name, out string? value) && !string.IsNullOrEmpty(value) ? value : null;
}
