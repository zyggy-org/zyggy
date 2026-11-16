using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Git;
using Zyggy.Core.Registry;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Registry;

/// <summary>Spec 45 slice 2: a <c>devRoots</c> entry inside another entry is scanned too, with a <c>root nested</c> notice.</summary>
public sealed class DiscoveryScannerNestedTests : IDisposable
{
    private readonly ScanTree _tree = new();
    private readonly RecordingProcessRunner _processes = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero));
    private readonly RecordingLogger<DiscoveryScanner> _log = new();

    public void Dispose() => _tree.Dispose();

    private DiscoveryScanner Scanner(int depth) =>
        new(new GitClient(_processes, new GitClientOptions(), _clock), DiscoveryScannerTests.Patterns, new DiscoveryOptions { Depth = depth }, _clock, _log);

    private static Task<ScanResult> ScanAsync(DiscoveryScanner scanner, params string[] roots) =>
        scanner.ScanAsync(roots, DiscoveryDetail.Full, TestContext.Current.CancellationToken);

    private static IEnumerable<string> Names(ScanResult result) => result.Projects.Select(p => p.Name);

    // AC-6: roots `A` and `A/G`, depth 1 — children of both published.
    [Fact]
    public async Task ScanAsync_NestedRootDepthOne_ChildrenOfBothPublished()
    {
        // Arrange
        string a = _tree.Root("a");
        _tree.Project(a, "q", git: false);
        string g = _tree.Folder(a, "g");
        _tree.Project(g, "p", git: false);

        // Act
        ScanResult result = await ScanAsync(Scanner(1), a, g);

        // Assert
        Names(result).Should().Equal("p", "q");
    }

    // AC-6: one `root nested <root> under <parent>` notice.
    [Fact]
    public async Task ScanAsync_NestedRoot_OneRootNestedNotice()
    {
        // Arrange
        string a = _tree.Root("a");
        _tree.Project(a, "q", git: false);
        string g = _tree.Folder(a, "g");
        _tree.Project(g, "p", git: false);

        // Act
        ScanResult result = await ScanAsync(Scanner(1), a, g);

        // Assert
        result.Notices.Should().Equal(new ScanNotice(ScanNoticeKind.RootNested, ScanTree.Published(g), ScanTree.Published(a)));
    }

    // AC-6: a project reached from both roots (depth 2) is listed once, with no duplicate-name notice.
    [Fact]
    public async Task ScanAsync_NestedRootDepthTwo_ProjectReachedTwiceListedOnce()
    {
        // Arrange
        string a = _tree.Root("a");
        _tree.Project(a, "q", git: false);
        string g = _tree.Folder(a, "g");
        string p = _tree.Project(g, "p", git: false);

        // Act
        ScanResult result = await ScanAsync(Scanner(2), a, g);

        // Assert
        result.Projects.Select(x => x.Path).Should().Equal(ScanTree.Published(p), ScanTree.Published(Path.Combine(a, "q")));
        result.Notices.Select(n => n.Kind).Should().Equal(ScanNoticeKind.RootNested);
    }

    // AC-6 / Q4: the innermost root gives the level — `A/G/devops` is level 1 from nested root `A/G`, so it wins over level-2 `A/H/devops`.
    [Fact]
    public async Task ScanAsync_NestedRoot_LevelFromInnermostRootWinsName()
    {
        // Arrange
        string a = _tree.Root("a");
        string g = _tree.Folder(a, "g");
        string inner = _tree.Project(g, "devops", git: false);
        _tree.Project(Path.Combine(a, "h"), "devops", git: false);

        // Act
        ScanResult result = await ScanAsync(Scanner(2), a, g);

        // Assert
        result.Projects.Select(x => x.Path).Should().Equal(ScanTree.Published(inner));
    }

    // AC-6: the notice names the closest enclosing root.
    [Fact]
    public async Task ScanAsync_ThreeNestedRoots_NoticeNamesClosestParent()
    {
        // Arrange
        string a = _tree.Root("a");
        string g = _tree.Folder(a, "g");
        string h = _tree.Folder(g, "h");

        // Act
        ScanResult result = await ScanAsync(Scanner(1), a, g, h);

        // Assert
        result.Notices.Should().Equal(
            new ScanNotice(ScanNoticeKind.RootNested, ScanTree.Published(g), ScanTree.Published(a)),
            new ScanNotice(ScanNoticeKind.RootNested, ScanTree.Published(h), ScanTree.Published(g)));
    }

    // AC-6: on Windows `d:/x/A/B` and `D:/X/a/b` are the same folder.
    [Fact]
    public async Task ScanAsync_NestedRootOtherCaseOnWindows_SameFolder()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows paths compare case-insensitively; elsewhere they do not");

        // Arrange
        string a = _tree.Root("a");
        string g = _tree.Folder(a, "g");
        _tree.Project(g, "p", git: false);

        // Act
        ScanResult result = await ScanAsync(Scanner(2), a, g.ToUpperInvariant(), g);

        // Assert
        Names(result).Should().Equal("p");
        result.Notices.Select(n => n.Kind).Should().Equal(ScanNoticeKind.RootNested);
    }

    // AC-7: a nested root that is itself a project of its outer root is published, and its children are scanned as a root.
    [Fact]
    public async Task ScanAsync_NestedRootIsProject_RootAndChildPublished()
    {
        // Arrange
        string a = _tree.Root("a");
        string b = _tree.Project(a, "b", git: false);
        _tree.Project(b, "c", git: false);

        // Act
        ScanResult result = await ScanAsync(Scanner(2), a, b);

        // Assert
        Names(result).Should().Equal("b", "c");
    }
}
