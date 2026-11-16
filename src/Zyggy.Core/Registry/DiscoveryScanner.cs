using System.Globalization;
using System.IO.Enumeration;

using Microsoft.Extensions.Logging;

using Zyggy.Core.Git;
using Zyggy.Core.Memory;

namespace Zyggy.Core.Registry;

/// <summary>
/// Scans every <c>devRoots</c> entry to <see cref="DiscoveryOptions.Depth"/> levels for Claude Code projects (a folder holding
/// <c>.claude/</c>) and builds the registry's project list (founding spec §4 "Registry file", specs 14 and 45). Links are never followed,
/// hidden, built-in or excluded folders and folders inside a project are not listed, and one scan looks into at most
/// <see cref="DiscoveryOptions.MaxFolders"/> folders; a root inside another root is scanned too, with a notice, and a folder
/// reached from both counts from the innermost root; of folders deriving one name the shallowest keeps it, and a tie at that level
/// publishes none. Never throws for a file-system or git failure: every such failure becomes a <see cref="ScanNotice"/>.
/// </summary>
public sealed partial class DiscoveryScanner
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly GitClient _git;
    private readonly SecretPatterns? _patterns;
    private readonly DiscoveryOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<DiscoveryScanner> _log;
    private readonly Func<string, IEnumerable<DirectoryInfo>> _listChildren;

    internal DiscoveryScanner(GitClient git, SecretPatterns? patterns, DiscoveryOptions options, TimeProvider clock, ILogger<DiscoveryScanner> log)
        : this(git, patterns, options, clock, log, null)
    {
    }

    /// <summary>Test hook (A7): <paramref name="listChildren"/> replaces the listing of a folder's direct children (a root or a folder below it).</summary>
    internal DiscoveryScanner(
        GitClient git,
        SecretPatterns? patterns,
        DiscoveryOptions options,
        TimeProvider clock,
        ILogger<DiscoveryScanner> log,
        Func<string, IEnumerable<DirectoryInfo>>? listChildren)
    {
        _git = git;
        _patterns = patterns;
        _options = options;
        _clock = clock;
        _log = log;
        _listChildren = listChildren ?? (root => new DirectoryInfo(root).EnumerateDirectories());
    }

    /// <summary>Scans the roots.</summary>
    /// <param name="devRoots">The <c>devRoots</c> entries of <c>node.json</c>.</param>
    /// <param name="detail">The requested detail; <see cref="DiscoveryDetail.Full"/> runs as <see cref="DiscoveryDetail.Names"/> without secret patterns.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>The projects and the notices.</returns>
    public async Task<ScanResult> ScanAsync(IReadOnlyList<string> devRoots, DiscoveryDetail detail, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(devRoots);
        long started = _clock.GetTimestamp();
        var notices = new List<ScanNotice>();

        DiscoveryDetail effective = detail;
        if (detail == DiscoveryDetail.Full && _patterns is null)
        {
            effective = DiscoveryDetail.Names;
            notices.Add(new ScanNotice(ScanNoticeKind.PatternsUnavailable, "ZYGGY_SECRET_PATTERNS", "descriptions and summaries not published"));
        }

        var found = new List<Candidate>();
        var walk = new WalkCount();
        foreach (string root in NormaliseRoots(devRoots, notices))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (walk.CapReached)
            {
                break;
            }

            WalkRoot(root, found, notices, walk, cancellationToken);
        }

        // A folder reached from a root and from a nested root is one project; the innermost root gives its level (spec 45 Q4).
        IEnumerable<Candidate> merged = found
            .GroupBy(f => f.Path, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .Select(g => g.MaxBy(c => c.Root.Length)!);

        var unique = new List<(string Name, string Path)>();
        foreach (IGrouping<string, Candidate> group in merged.GroupBy(f => f.Name, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            if (group.Count() == 1)
            {
                Candidate only = group.Single();
                unique.Add((only.Name, only.Path));
                continue;
            }

            // Shallowest wins (spec 45 Q9): the one candidate at the lowest level keeps the name; a tie at that level publishes none.
            int lowest = group.Min(c => c.Level);
            List<Candidate> shallowest = group.Where(c => c.Level == lowest).ToList();
            Candidate? winner = shallowest.Count == 1 ? shallowest[0] : null;
            IEnumerable<string> others = group.Where(c => c != winner).Select(c => Published(c.Path)).Order(StringComparer.Ordinal);
            string paths = string.Join("; ", winner is null ? others : others.Prepend(Published(winner.Path)));
            notices.Add(new ScanNotice(ScanNoticeKind.DuplicateName, group.Key, paths));
            if (winner is not null)
            {
                unique.Add((winner.Name, winner.Path));
            }
        }

        if (unique.Count > _options.MaxProjects)
        {
            notices.Add(new ScanNotice(
                ScanNoticeKind.ProjectCapReached,
                _options.MaxProjects.ToString(CultureInfo.InvariantCulture),
                $"{unique.Count - _options.MaxProjects} projects left out"));
            unique = unique.Take(_options.MaxProjects).ToList();
        }

        var projects = new List<RegistryProject>(unique.Count);
        foreach ((string name, string path) in unique)
        {
            cancellationToken.ThrowIfCancellationRequested();
            projects.Add(await ProjectAsync(name, path, effective, notices, cancellationToken).ConfigureAwait(false));
        }

        TimeSpan elapsed = _clock.GetElapsedTime(started);
        if (elapsed > _options.SlowScanThreshold)
        {
            notices.Add(new ScanNotice(
                ScanNoticeKind.SlowScan,
                devRoots.Count.ToString(CultureInfo.InvariantCulture) + " roots",
                ((int)elapsed.TotalSeconds).ToString(CultureInfo.InvariantCulture) + " s"));
        }

        LogScanned(_log, projects.Count, notices.Count, walk.Visited, _options.Depth);
        return new ScanResult(projects, notices, elapsed, effective);
    }

    /// <summary>The path as a registry file writes it: full, <c>/</c> separators.</summary>
    internal static string Published(string path) => path.Replace('\\', '/');

    private static bool IsReparsePoint(FileSystemInfo info) => (info.Attributes & FileAttributes.ReparsePoint) != 0;

    /// <summary>
    /// The roots, full and de-duplicated (case-folded on Windows). A root inside another root is kept and scanned too, with one
    /// <see cref="ScanNoticeKind.RootNested"/> notice naming its closest enclosing root (spec 45).
    /// </summary>
    private static List<string> NormaliseRoots(IReadOnlyList<string> devRoots, List<ScanNotice> notices)
    {
        var roots = new List<string>();
        foreach (string entry in devRoots)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            string full;
            try
            {
                full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (!roots.Any(r => string.Equals(r, full, PathComparison)))
            {
                roots.Add(full);
            }
        }

        foreach (string root in roots)
        {
            string? parent = roots.Where(other => IsUnder(root, other)).MaxBy(other => other.Length);
            if (parent is not null)
            {
                notices.Add(new ScanNotice(ScanNoticeKind.RootNested, Published(root), Published(parent)));
            }
        }

        return roots;
    }

    private static bool IsUnder(string path, string root) =>
        path.Length > root.Length
        && path.StartsWith(root, PathComparison)
        && (path[root.Length] == Path.DirectorySeparatorChar || path[root.Length] == Path.AltDirectorySeparatorChar);

    /// <summary>
    /// Walks one root breadth-first to <see cref="DiscoveryOptions.Depth"/> levels (root children are level 1, spec 45). A folder holding a
    /// real <c>.claude/</c> is a project and is not descended into; hidden folders and links are skipped. Folders are visited in ordinal order.
    /// </summary>
    private void WalkRoot(string root, List<Candidate> found, List<ScanNotice> notices, WalkCount walk, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root))
        {
            notices.Add(new ScanNotice(ScanNoticeKind.RootMissing, Published(root), null));
            return;
        }

        var queue = new Queue<(string Folder, int Level)>();
        queue.Enqueue((root, 1));
        while (queue.TryDequeue(out (string Folder, int Level) next))
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<DirectoryInfo> children;
            try
            {
                children = [.. _listChildren(next.Folder).OrderBy(c => c.Name, StringComparer.Ordinal)];
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                ScanNoticeKind kind = next.Level == 1 ? ScanNoticeKind.RootUnreadable : ScanNoticeKind.FolderUnreadable;
                notices.Add(new ScanNotice(kind, Published(next.Folder), exception.GetType().Name));
                continue;
            }

            foreach (DirectoryInfo child in children)
            {
                try
                {
                    if (child.Name.StartsWith('.') || IsReparsePoint(child))
                    {
                        continue;
                    }

                    // The folder cap bounds the .claude checks of one scan over all roots (spec 45 Bounds).
                    if (walk.Visited >= _options.MaxFolders)
                    {
                        walk.CapReached = true;
                        notices.Add(new ScanNotice(
                            ScanNoticeKind.FolderCapReached,
                            Published(root),
                            _options.MaxFolders.ToString(CultureInfo.InvariantCulture)));
                        return;
                    }

                    walk.Visited++;
                    var claude = new DirectoryInfo(Path.Join(child.FullName, ".claude"));
                    if (!claude.Exists)
                    {
                        if (next.Level < _options.Depth && !IsSkipped(child.Name))
                        {
                            queue.Enqueue((child.FullName, next.Level + 1));
                        }

                        continue;
                    }

                    if (IsReparsePoint(claude))
                    {
                        continue; // a linked .claude marks no project, and nothing is read through it (spec 45 Q7)
                    }

                    string name = ProjectName.Derive(child.Name);
                    string path = Path.TrimEndingDirectorySeparator(child.FullName);
                    if (!ProjectName.IsLabel(name))
                    {
                        notices.Add(new ScanNotice(ScanNoticeKind.NameUnusable, Published(path), null));
                        continue;
                    }

                    found.Add(new Candidate(name, path, next.Level, root));
                }
                catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                {
                    notices.Add(new ScanNotice(ScanNoticeKind.FolderUnreadable, Published(child.FullName), exception.GetType().Name));
                }
            }
        }
    }

    /// <summary>
    /// Whether the walk never descends into a folder of this name: the built-in list or a <see cref="DiscoveryOptions.Exclude"/> glob
    /// (spec 45). Checked only before descending, never for a project's own name. Case is ignored on Windows only.
    /// </summary>
    private bool IsSkipped(string name)
    {
        bool ignoreCase = OperatingSystem.IsWindows();
        return BuiltInSkips.Concat(_options.Exclude).Any(glob => FileSystemName.MatchesSimpleExpression(glob, name, ignoreCase));
    }

    /// <summary>Folders the walk never descends into, whatever <c>discovery.exclude</c> says (spec 45 Q3).</summary>
    private static readonly string[] BuiltInSkips =
        ["node_modules", "bin", "obj", "dist", "out", "target", "packages", "TestResults", "__pycache__", "*.worktrees"];

    /// <summary>The folder count of one scan, shared by its roots.</summary>
    private sealed class WalkCount
    {
        public int Visited { get; set; }

        public bool CapReached { get; set; }
    }

    /// <summary>A project folder found by the walk, with its level below the root it was found from.</summary>
    private sealed record Candidate(string Name, string Path, int Level, string Root);

    private async Task<RegistryProject> ProjectAsync(string name, string path, DiscoveryDetail detail, List<ScanNotice> notices, CancellationToken cancellationToken)
    {
        string? branch = await DefaultBranchAsync(path, cancellationToken).ConfigureAwait(false);
        if (detail == DiscoveryDetail.Names)
        {
            return new RegistryProject(name, null, branch, [], [], null);
        }

        IReadOnlyList<RegistryEntry> agents = ReadAgents(name, path, notices);
        IReadOnlyList<RegistryEntry> skills = ReadSkills(name, path, notices);
        string? summary = ReadSummary(name, path, notices);
        return new RegistryProject(name, Published(path), branch, agents, skills, summary);
    }

    private async Task<string?> DefaultBranchAsync(string path, CancellationToken cancellationToken)
    {
        string gitPath = Path.Join(path, ".git");
        if (!Directory.Exists(gitPath) && !File.Exists(gitPath))
        {
            return null;
        }

        // GitClient turns a failed or unstartable git into a result, never an exception: a failure reads as "no branch".
        return await _git.DefaultBranchAsync(path, cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "discover: scanned {Projects} projects, {Notices} notices, {Folders} folders visited, depth {Depth}")]
    private static partial void LogScanned(ILogger logger, int projects, int notices, int folders, int depth);
}
