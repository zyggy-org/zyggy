using System.Globalization;

using Microsoft.Extensions.Logging;

using Zyggy.Core.Git;
using Zyggy.Core.Memory;

namespace Zyggy.Core.Registry;

/// <summary>
/// Scans the direct children of every <c>devRoots</c> entry for Claude Code projects (a folder holding <c>.claude/</c>) and builds the
/// registry's project list (founding spec §4 "Registry file" as amended by A14-1, spec 14). Links are never followed, hidden folders
/// and nested projects are not listed, two folders deriving one name are both left out. Never throws for a file-system or git failure:
/// every such failure becomes a <see cref="ScanNotice"/>.
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

    /// <summary>Test hook (A7): <paramref name="listChildren"/> replaces the listing of a root's direct children.</summary>
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

        var found = new List<(string Name, string Path)>();
        foreach (string root in NormaliseRoots(devRoots))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ListProjects(root, found, notices);
        }

        var unique = new List<(string Name, string Path)>();
        foreach (IGrouping<string, (string Name, string Path)> group in found.GroupBy(f => f.Name, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            if (group.Count() > 1)
            {
                string paths = string.Join("; ", group.Select(g => Published(g.Path)).Order(StringComparer.Ordinal));
                notices.Add(new ScanNotice(ScanNoticeKind.DuplicateName, group.Key, paths));
                continue;
            }

            unique.Add(group.Single());
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

        LogScanned(_log, projects.Count, notices.Count);
        return new ScanResult(projects, notices, elapsed, effective);
    }

    /// <summary>The path as a registry file writes it: full, <c>/</c> separators.</summary>
    internal static string Published(string path) => path.Replace('\\', '/');

    private static bool IsReparsePoint(FileSystemInfo info) => (info.Attributes & FileAttributes.ReparsePoint) != 0;

    private static List<string> NormaliseRoots(IReadOnlyList<string> devRoots)
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

        // A root lying under another root is scanned as part of that root only (Edge Cases).
        return roots.Where(r => !roots.Any(other => !ReferenceEquals(other, r) && IsUnder(r, other))).ToList();
    }

    private static bool IsUnder(string path, string root) =>
        path.Length > root.Length
        && path.StartsWith(root, PathComparison)
        && (path[root.Length] == Path.DirectorySeparatorChar || path[root.Length] == Path.AltDirectorySeparatorChar);

    private void ListProjects(string root, List<(string Name, string Path)> found, List<ScanNotice> notices)
    {
        if (!Directory.Exists(root))
        {
            notices.Add(new ScanNotice(ScanNoticeKind.RootMissing, Published(root), null));
            return;
        }

        List<DirectoryInfo> children;
        try
        {
            children = [.. _listChildren(root)];
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            notices.Add(new ScanNotice(ScanNoticeKind.RootUnreadable, Published(root), exception.GetType().Name));
            return;
        }

        foreach (DirectoryInfo child in children)
        {
            try
            {
                if (child.Name.StartsWith('.') || IsReparsePoint(child))
                {
                    continue;
                }

                var claude = new DirectoryInfo(Path.Join(child.FullName, ".claude"));
                if (!claude.Exists || IsReparsePoint(claude))
                {
                    continue;
                }

                string name = ProjectName.Derive(child.Name);
                string path = Path.TrimEndingDirectorySeparator(child.FullName);
                if (!ProjectName.IsLabel(name))
                {
                    notices.Add(new ScanNotice(ScanNoticeKind.NameUnusable, Published(path), null));
                    continue;
                }

                found.Add((name, path));
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                notices.Add(new ScanNotice(ScanNoticeKind.RootUnreadable, Published(child.FullName), exception.GetType().Name));
            }
        }
    }

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

    [LoggerMessage(Level = LogLevel.Debug, Message = "discover: scanned {Projects} projects, {Notices} notices")]
    private static partial void LogScanned(ILogger logger, int projects, int notices);
}
