using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Git;
using Zyggy.Core.Registry;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Registry;

/// <summary>
/// Spec 45 slice 3: the bounded walk — folders never descended into, links never followed, a folder that cannot be listed, the folder cap
/// and the verbose line.
/// </summary>
public sealed class DiscoveryScannerBoundsTests : IDisposable
{
    private readonly ScanTree _tree = new();
    private readonly RecordingProcessRunner _processes = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero));
    private readonly RecordingLogger<DiscoveryScanner> _log = new();

    public void Dispose() => _tree.Dispose();

    private DiscoveryScanner Scanner(DiscoveryOptions? options = null, Func<string, IEnumerable<DirectoryInfo>>? listChildren = null) =>
        new(new GitClient(_processes, new GitClientOptions(), _clock), DiscoveryScannerTests.Patterns, options ?? new DiscoveryOptions(), _clock, _log, listChildren);

    private static Task<ScanResult> ScanAsync(DiscoveryScanner scanner, params string[] roots) =>
        scanner.ScanAsync(roots, DiscoveryDetail.Full, TestContext.Current.CancellationToken);

    private static IEnumerable<string> Names(ScanResult result) => result.Projects.Select(p => p.Name);

    // AC-9: hidden folders and the built-in list are never descended into.
    [Theory]
    [InlineData("node_modules")]
    [InlineData(".git")]
    [InlineData("foo.worktrees")]
    [InlineData("bin")]
    [InlineData("obj")]
    [InlineData("dist")]
    [InlineData("out")]
    [InlineData("target")]
    [InlineData("packages")]
    [InlineData("TestResults")]
    [InlineData("__pycache__")]
    public async Task ScanAsync_ProjectInsideBuiltInFolder_NotPublished(string folder)
    {
        // Arrange
        string a = _tree.Root("a");
        _tree.Project(a, "kept", git: false);
        _tree.Project(Path.Combine(a, folder), "x", git: false);

        // Act
        ScanResult result = await ScanAsync(Scanner(), a);

        // Assert
        Names(result).Should().Equal("kept");
    }

    // AC-9: `discovery.exclude` adds folder-name globs.
    [Fact]
    public async Task ScanAsync_ExcludeGlob_FolderNotDescended()
    {
        // Arrange
        string a = _tree.Root("a");
        _tree.Project(Path.Combine(a, "archive-2024"), "x", git: false);
        _tree.Project(Path.Combine(a, "group"), "y", git: false);

        // Act
        ScanResult result = await ScanAsync(Scanner(new DiscoveryOptions { Exclude = ["archive*"] }), a);

        // Assert
        Names(result).Should().Equal("y");
    }

    // AC-9: an exclude entry cannot re-enable a built-in.
    [Fact]
    public async Task ScanAsync_ExcludeNamingBuiltIn_StillNotDescended()
    {
        // Arrange
        string a = _tree.Root("a");
        _tree.Project(Path.Combine(a, "node_modules"), "x", git: false);

        // Act
        ScanResult result = await ScanAsync(Scanner(new DiscoveryOptions { Exclude = ["!node_modules", "node_modules"] }), a);

        // Assert
        result.Projects.Should().BeEmpty();
    }

    // Spec 55 AC-1: a project folder matching the owner's `discovery.exclude` is not published.
    [Fact]
    public async Task ScanAsync_ProjectFolderMatchingExclude_NotPublished()
    {
        // Arrange
        string a = _tree.Root("a");
        _tree.Project(a, "home", git: false);
        _tree.Project(a, "instance", git: false);

        // Act
        ScanResult result = await ScanAsync(Scanner(new DiscoveryOptions { Exclude = ["home"] }), a);

        // Assert
        Names(result).Should().Equal("instance");
    }

    // Spec 55 AC-1: without the exclude the same project folder is published.
    [Fact]
    public async Task ScanAsync_ProjectFolderNoExclude_Published()
    {
        // Arrange
        string a = _tree.Root("a");
        _tree.Project(a, "home", git: false);
        _tree.Project(a, "instance", git: false);

        // Act
        ScanResult result = await ScanAsync(Scanner(), a);

        // Assert
        Names(result).Should().Equal("home", "instance");
    }

    // Spec 55 AC-1 (edge case): with `naming: path` the glob still matches the folder's own name, not the joined project name.
    [Fact]
    public async Task ScanAsync_ExcludeGlobWithPathNaming_MatchesOwnName()
    {
        // Arrange
        string a = _tree.Root("a");
        string group = _tree.Folder(a, "group");
        _tree.Project(group, "home", git: false);
        _tree.Project(group, "instance", git: false);

        // Act
        ScanResult result = await ScanAsync(Scanner(new DiscoveryOptions { Exclude = ["ho*"], Naming = DiscoveryNaming.Path }), a);

        // Assert
        result.Projects.Select(p => p.Path).Should().Equal(ScanTree.Published(Path.Combine(group, "instance")));
    }

    // Spec 45 AC-9, kept by spec 55 AC-2: a project's own name is never checked against the built-in list.
    [Fact]
    public async Task ScanAsync_ProjectNamedOutAtLevelOne_Found()
    {
        // Arrange
        string a = _tree.Root("a");
        _tree.Project(a, "out", git: false);

        // Act
        ScanResult result = await ScanAsync(Scanner(), a);

        // Assert
        Names(result).Should().Equal("out");
    }

    // AC-11: a directory link below level 1 is not followed.
    [Fact]
    public async Task ScanAsync_LinkAtLevelTwoToProject_NotFollowed()
    {
        // Arrange
        string a = _tree.Root("a");
        string target = _tree.Project(_tree.Root("elsewhere"), "secret-project", git: false);
        string group = _tree.Folder(a, "group");
        bool linked = _tree.Link(Path.Combine(group, "linked"), target);
        Assert.SkipUnless(linked, "symbolic links need a privilege on this machine");

        // Act
        ScanResult result = await ScanAsync(Scanner(), a);

        // Assert
        result.Projects.Should().BeEmpty();
    }

    // AC-11: a link loop to an ancestor ends the scan and publishes nothing behind it.
    [Fact]
    public async Task ScanAsync_LinkLoopToAncestor_ScanEndsNothingBehindLink()
    {
        // Arrange
        string a = _tree.Root("a");
        string group = _tree.Folder(a, "group");
        _tree.Project(group, "p", git: false);
        bool linked = _tree.Link(Path.Combine(group, "loop"), a);
        Assert.SkipUnless(linked, "symbolic links need a privilege on this machine");

        // Act
        ScanResult result = await ScanAsync(Scanner(new DiscoveryOptions { Depth = 6 }), a);

        // Assert
        result.Projects.Select(p => p.Path).Should().Equal(ScanTree.Published(Path.Combine(group, "p")));
    }

    // AC-11: a project whose `.claude` is a link is not a project.
    [Fact]
    public async Task ScanAsync_ClaudeFolderIsLink_NotPublished()
    {
        // Arrange
        string a = _tree.Root("a");
        string real = _tree.Folder(_tree.Root("elsewhere"), ".claude");
        string project = _tree.Folder(_tree.Folder(a, "group"), "p");
        bool linked = _tree.Link(Path.Combine(project, ".claude"), real);
        Assert.SkipUnless(linked, "symbolic links need a privilege on this machine");

        // Act
        ScanResult result = await ScanAsync(Scanner(), a);

        // Assert
        result.Projects.Should().BeEmpty();
    }

    // AC-12: a folder below a root that cannot be listed gives `folder unreadable`; the scan continues.
    [Fact]
    public async Task ScanAsync_FolderBelowRootUnreadable_NoticeAndOthersPublished()
    {
        // Arrange
        string a = _tree.Root("a");
        string locked = _tree.Folder(a, "g");
        _tree.Project(locked, "hidden-from-us", git: false);
        _tree.Project(_tree.Folder(a, "h"), "p", git: false);
        _tree.Project(a, "q", git: false);
        IEnumerable<DirectoryInfo> List(string folder) =>
            string.Equals(Path.GetFullPath(folder), Path.GetFullPath(locked), StringComparison.Ordinal)
                ? throw new UnauthorizedAccessException("denied")
                : new DirectoryInfo(folder).EnumerateDirectories();

        // Act
        ScanResult result = await ScanAsync(Scanner(listChildren: List), a);

        // Assert
        Names(result).Should().Equal("p", "q");
        result.Notices.Should().Equal(new ScanNotice(ScanNoticeKind.FolderUnreadable, ScanTree.Published(locked), nameof(UnauthorizedAccessException)));
    }

    // AC-12: a root that cannot be listed keeps its own notice.
    [Fact]
    public async Task ScanAsync_RootUnreadable_RootNoticeKept()
    {
        // Arrange
        string a = _tree.Root("a");

        // Act
        ScanResult result = await ScanAsync(Scanner(listChildren: _ => throw new UnauthorizedAccessException("denied")), a);

        // Assert
        result.Notices.Select(n => n.Kind).Should().Equal(ScanNoticeKind.RootUnreadable);
    }

    // AC-13: the folder cap stops the walk with a notice; the projects found so far are published.
    [Fact]
    public async Task ScanAsync_MoreFoldersThanCap_NoticeAndProjectsSoFar()
    {
        // Arrange
        string a = _tree.Root("a");
        foreach (string name in new[] { "p1", "p2", "p3", "p4", "p5", "p6", "p7" })
        {
            _tree.Project(a, name, git: false);
        }

        // Act
        ScanResult result = await ScanAsync(Scanner(new DiscoveryOptions { MaxFolders = 5 }), a);

        // Assert
        Names(result).Should().Equal("p1", "p2", "p3", "p4", "p5");
        result.Notices.Should().Equal(new ScanNotice(ScanNoticeKind.FolderCapReached, ScanTree.Published(a), "5"));
    }

    // AC-13: exactly as many folders as the cap is no notice.
    [Fact]
    public async Task ScanAsync_FoldersEqualCap_NoNotice()
    {
        // Arrange
        string a = _tree.Root("a");
        _tree.Project(a, "p1", git: false);
        _tree.Project(a, "p2", git: false);

        // Act
        ScanResult result = await ScanAsync(Scanner(new DiscoveryOptions { MaxFolders = 2 }), a);

        // Assert
        result.Notices.Should().BeEmpty();
    }

    // AC-13: the cap counts across roots; the second root is not walked once it is reached.
    [Fact]
    public async Task ScanAsync_CapReachedInFirstRoot_SecondRootNotWalked()
    {
        // Arrange
        string a = _tree.Root("a");
        _tree.Project(a, "p1", git: false);
        _tree.Project(a, "p2", git: false);
        string b = _tree.Root("b");
        _tree.Project(b, "q1", git: false);

        // Act
        ScanResult result = await ScanAsync(Scanner(new DiscoveryOptions { MaxFolders = 2 }), a, b);

        // Assert
        Names(result).Should().Equal("p1", "p2");
        result.Notices.Select(n => n.Kind).Should().Equal(ScanNoticeKind.FolderCapReached);
    }

    // AC-13: a cap reached inside the first root gives one notice, and the next root is not walked.
    [Fact]
    public async Task ScanAsync_CapReachedInsideFirstRoot_OneNoticeOnly()
    {
        // Arrange
        string a = _tree.Root("a");
        _tree.Project(a, "p1", git: false);
        _tree.Project(a, "p2", git: false);
        string b = _tree.Root("b");
        _tree.Project(b, "q1", git: false);

        // Act
        ScanResult result = await ScanAsync(Scanner(new DiscoveryOptions { MaxFolders = 1 }), a, b);

        // Assert
        result.Notices.Should().Equal(new ScanNotice(ScanNoticeKind.FolderCapReached, ScanTree.Published(a), "1"));
    }

    // AC-14: the Debug line names the folders visited and the depth.
    [Fact]
    public async Task ScanAsync_Done_DebugLineFoldersVisitedAndDepth()
    {
        // Arrange
        string a = _tree.Root("a");
        _tree.Project(_tree.Folder(a, "g"), "p", git: false);
        _tree.Project(a, "q", git: false);

        // Act
        await ScanAsync(Scanner(), a);

        // Assert
        _log.AtLevel(LogLevel.Debug).Select(e => e.Message).Should().Equal("discover: scanned 2 projects, 0 notices, 3 folders visited, depth 2");
    }
}
