namespace Zyggy.Core.M365.Tools;

/// <summary>The outcome of <see cref="M365ToolPartition.Load"/>.</summary>
internal sealed record M365ToolPartitionLoad(M365ToolPartition? Partition, string? Error);

/// <summary>
/// The pinned m365 server's tool partition as template data (spec 33 AC-27, owner decision 8): <c>.claude/skills/m365/tools/</c>
/// <c>enabled.txt</c> (loaded by the server), <c>excluded.txt</c> (every other tool), <c>actions.txt</c> (the D7 action tools),
/// <c>auth.txt</c> (the auth tools) and <c>server-version.txt</c>. The binary holds no copy. From it come the server's
/// <c>ENABLED_TOOLS</c> filter and the three unattended runs' allow and deny lists — <c>m365-lib.sh</c>'s rules with each script rule
/// replaced by its <c>zyggy m365</c> verb.
/// </summary>
internal sealed class M365ToolPartition
{
    private const string Prefix = "mcp__m365__";
    private const string StateRead = "Read(~/.local/state/zyggy/m365/**)";
    private const string State = "Bash(zyggy m365 state *)";
    private const string Facts = "Bash(zyggy m365 facts *)";
    private const string Parse = "Bash(zyggy m365 parse *)";

    // What no run may ever use: the identity's own verbs and every outbound channel. Each deny list ends with these.
    private static readonly string[] CommonDeny =
    [
        "Bash(zyggy m365 auth-header*)", "Bash(zyggy m365 token-test*)", "Bash(zyggy m365 cert-init*)", "Bash(zyggy m365 mcp-server*)",
        "Bash(zyggy m365 brief*)", "Bash(zyggy m365 mail-backfill*)", "Bash(zyggy m365 files-backfill*)",
        "WebFetch", "WebSearch", "mcp__plugin_playwright_playwright", "Edit", "Write", "NotebookEdit",
        "Bash(curl *)", "Bash(wget *)", "Bash(git *)", "Bash(npm *)", "Bash(npx *)", "Bash(node *)",
    ];

    private M365ToolPartition(string[] enabled, string[] excluded, string[] actions, string[] auth, string serverVersion)
    {
        Enabled = enabled;
        Excluded = excluded;
        Actions = actions;
        Auth = auth;
        ServerVersion = serverVersion;
    }

    public IReadOnlyList<string> Enabled { get; }

    public IReadOnlyList<string> Excluded { get; }

    public IReadOnlyList<string> Actions { get; }

    public IReadOnlyList<string> Auth { get; }

    public string ServerVersion { get; }

    /// <summary>Gets the server's <c>ENABLED_TOOLS</c>: the enabled names, anchored.</summary>
    public string EnabledToolsRegex => "^(" + string.Join('|', Enabled) + ")$";

    /// <summary>Gets the brief's allow list: the enabled read and Draft tools (never an action tool), state, facts, parse, reads of the state dir.</summary>
    public IReadOnlyList<string> BriefAllow => [.. Enabled.Where(t => !Actions.Contains(t)).Select(t => Prefix + t), State, Facts, Parse, StateRead];

    /// <summary>Gets the brief's deny list: every excluded tool, the enabled action tools, the common deny list.</summary>
    public IReadOnlyList<string> BriefDeny => [.. Excluded.Select(t => Prefix + t), .. Enabled.Where(Actions.Contains).Select(t => Prefix + t), .. CommonDeny];

    /// <summary>Gets the mail backfill's allow list: the shared-mailbox read tools, state, facts, reads of the state dir.</summary>
    public IReadOnlyList<string> MailBackfillAllow => [.. Enabled.Where(IsMailRead).Select(t => Prefix + t), State, Facts, StateRead];

    /// <summary>Gets the mail backfill's deny list: every excluded tool, every other enabled tool, parse, the common deny list.</summary>
    public IReadOnlyList<string> MailBackfillDeny => [.. Excluded.Select(t => Prefix + t), .. Enabled.Where(t => !IsMailRead(t)).Select(t => Prefix + t), Parse, .. CommonDeny];

    /// <summary>Gets the files backfill's allow list: <c>download-bytes-to-file</c>, facts, parse.</summary>
    public IReadOnlyList<string> FilesBackfillAllow => [.. Enabled.Where(IsDownload).Select(t => Prefix + t), Facts, Parse];

    /// <summary>Gets the files backfill's deny list: every excluded tool, every other enabled tool, state, the common deny list.</summary>
    public IReadOnlyList<string> FilesBackfillDeny => [.. Excluded.Select(t => Prefix + t), .. Enabled.Where(t => !IsDownload(t)).Select(t => Prefix + t), State, .. CommonDeny];

    public static M365ToolPartitionLoad Load(string checkout)
    {
        ArgumentException.ThrowIfNullOrEmpty(checkout);
        var directory = Path.Join(checkout, ".claude", "skills", "m365", "tools");
        string[]? Read(string name, out string? error)
        {
            var path = Path.Join(directory, name);
            if (!File.Exists(path))
            {
                error = $"configuration error: {path} is missing";
                return null;
            }

            var text = File.ReadAllText(path);
            if (text.Contains('\r', StringComparison.Ordinal) || (text.Length > 0 && !text.EndsWith('\n')))
            {
                error = $"configuration error: {path} is not one name per line";
                return null;
            }

            var names = text.Length == 0 ? [] : text[..^1].Split('\n');
            if (names.Any(n => n.Length == 0 || n.Any(char.IsWhiteSpace)))
            {
                error = $"configuration error: {path} is not one name per line";
                return null;
            }

            if (!names.SequenceEqual(names.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)))
            {
                error = $"configuration error: {path} is not sorted and unique";
                return null;
            }

            error = null;
            return names;
        }

        string? error = null;
        var enabled = Read("enabled.txt", out error);
        var excluded = error is null ? Read("excluded.txt", out error) : null;
        var actions = error is null ? Read("actions.txt", out error) : null;
        var auth = error is null ? Read("auth.txt", out error) : null;
        var version = error is null ? Read("server-version.txt", out error) : null;
        if (error is not null)
        {
            return new M365ToolPartitionLoad(null, error);
        }

        if (enabled!.Intersect(excluded!).Any())
        {
            return new M365ToolPartitionLoad(null, "configuration error: tools: enabled and excluded overlap");
        }

        if (actions!.Any(a => !enabled.Contains(a) && !excluded.Contains(a)))
        {
            return new M365ToolPartitionLoad(null, "configuration error: tools: actions.txt names a tool outside the partition");
        }

        if (auth!.Any(a => !excluded.Contains(a)))
        {
            return new M365ToolPartitionLoad(null, "configuration error: tools: auth.txt names a tool that is not excluded");
        }

        return version!.Length != 1
            ? new M365ToolPartitionLoad(null, "configuration error: tools: server-version.txt must hold one version")
            : new M365ToolPartitionLoad(new M365ToolPartition(enabled!, excluded!, actions!, auth!, version[0]), null);
    }

    // The shared-mailbox read tools (the mail backfill's): list-/get-shared-mailbox-*.
    private static bool IsMailRead(string tool) =>
        tool.StartsWith("list-shared-mailbox-", StringComparison.Ordinal) || tool.StartsWith("get-shared-mailbox-", StringComparison.Ordinal);

    private static bool IsDownload(string tool) => tool == "download-bytes-to-file";
}
