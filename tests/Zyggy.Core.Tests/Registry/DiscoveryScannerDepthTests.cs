using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Git;
using Zyggy.Core.Registry;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Registry;

/// <summary>Spec 45 slice 1: <c>discovery.depth</c> (default 2), the walk below a root, and shallowest-wins names.</summary>
public sealed class DiscoveryScannerDepthTests : IDisposable
{
    private readonly ScanTree _tree = new();
    private readonly RecordingProcessRunner _processes = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero));
    private readonly RecordingLogger<DiscoveryScanner> _log = new();

    public void Dispose() => _tree.Dispose();

    private DiscoveryScanner Scanner(DiscoveryOptions options) =>
        new(new GitClient(_processes, new GitClientOptions(), _clock), DiscoveryScannerTests.Patterns, options, _clock, _log);

    private static Task<ScanResult> ScanAsync(DiscoveryScanner scanner, params string[] roots) =>
        scanner.ScanAsync(roots, DiscoveryDetail.Full, TestContext.Current.CancellationToken);

    private static IEnumerable<string> Names(ScanResult result) => result.Projects.Select(p => p.Name);

    // AC-1 / AC-12 (default): the default depth is 2.
    [Fact]
    public void DiscoveryOptions_Default_DepthTwo()
    {
        // Act
        var options = new DiscoveryOptions();

        // Assert
        options.Depth.Should().Be(2);
    }

    // AC-1: a level-2 project is found at depth 2 ...
    [Fact]
    public async Task ScanAsync_DefaultDepth_FindsLevelTwoProject()
    {
        // Arrange
        string a = _tree.Root("a");
        _tree.Project(Path.Combine(a, "group"), "deep", git: false);
        _tree.Project(a, "top", git: false);

        // Act
        ScanResult result = await ScanAsync(Scanner(new DiscoveryOptions()), a);

        // Assert
        Names(result).Should().Equal("deep", "top");
    }

    // AC-1: ... and not at depth 1.
    [Fact]
    public async Task ScanAsync_DepthOne_LevelTwoProjectNotFound()
    {
        // Arrange
        string a = _tree.Root("a");
        _tree.Project(Path.Combine(a, "group"), "deep", git: false);
        _tree.Project(a, "top", git: false);

        // Act
        ScanResult result = await ScanAsync(Scanner(new DiscoveryOptions { Depth = 1 }), a);

        // Assert
        Names(result).Should().Equal("top");
    }

    // AC-2: `A/B/p` found; `A/p2/sub` inside project `A/p2` not found (no descent into a project).
    [Fact]
    public async Task ScanAsync_DepthTwo_NeverDescendsIntoProject()
    {
        // Arrange
        string a = _tree.Root("a");
        string p = _tree.Project(Path.Combine(a, "b"), "p", git: false);
        string p2 = _tree.Project(a, "p2", git: false);
        _tree.Project(p2, "sub", git: false);

        // Act
        ScanResult result = await ScanAsync(Scanner(new DiscoveryOptions()), a);

        // Assert
        result.Projects.Select(x => x.Path).Should().Equal(ScanTree.Published(p), ScanTree.Published(p2));
    }

    // AC-2: the walk reaches exactly `Depth` levels.
    [Fact]
    public async Task ScanAsync_DepthThree_FindsLevelThreeNotFour()
    {
        // Arrange
        string a = _tree.Root("a");
        _tree.Project(Path.Combine(a, "x", "y"), "three", git: false);
        _tree.Project(Path.Combine(a, "x", "y", "z"), "four", git: false);

        // Act
        ScanResult result = await ScanAsync(Scanner(new DiscoveryOptions { Depth = 3 }), a);

        // Assert
        Names(result).Should().Equal("three");
    }

    // AC-4: shallowest wins — the level-1 project keeps the name, the deeper one is left out.
    [Fact]
    public async Task ScanAsync_LevelOneAndLevelTwoSameName_LevelOnePublished()
    {
        // Arrange
        string a = _tree.Root("a");
        string shallow = _tree.Project(a, "devops", git: false);
        _tree.Project(Path.Combine(a, "group"), "DevOps", git: false);

        // Act
        ScanResult result = await ScanAsync(Scanner(new DiscoveryOptions()), a);

        // Assert
        result.Projects.Select(x => x.Path).Should().Equal(ScanTree.Published(shallow));
    }

    // AC-4: the notice names both, the published one first.
    [Fact]
    public async Task ScanAsync_LevelOneAndLevelTwoSameName_NoticePublishedFirst()
    {
        // Arrange
        string a = _tree.Root("a");
        string deep = _tree.Project(Path.Combine(a, "a-group"), "devops", git: false); // sorts first in ordinal order
        string published = _tree.Project(a, "devops", git: false);

        // Act
        ScanResult result = await ScanAsync(Scanner(new DiscoveryOptions()), a);

        // Assert
        result.Notices.Should().ContainSingle().Which.Should().Be(new ScanNotice(
            ScanNoticeKind.DuplicateName,
            "devops",
            ScanTree.Published(published) + "; " + ScanTree.Published(deep)));
    }

    // AC-4: two at the same lowest level — neither published, one notice.
    [Fact]
    public async Task ScanAsync_TwoLevelTwoSameName_NeitherPublished()
    {
        // Arrange
        string a = _tree.Root("a");
        _tree.Project(Path.Combine(a, "g"), "devops", git: false);
        _tree.Project(Path.Combine(a, "h"), "devops", git: false);
        _tree.Project(a, "other", git: false);

        // Act
        ScanResult result = await ScanAsync(Scanner(new DiscoveryOptions()), a);

        // Assert
        Names(result).Should().Equal("other");
    }

    // AC-4: two at the same lowest level — exactly one duplicate notice naming both.
    [Fact]
    public async Task ScanAsync_TwoLevelTwoSameName_OneNotice()
    {
        // Arrange
        string a = _tree.Root("a");
        string g = _tree.Project(Path.Combine(a, "g"), "devops", git: false);
        string h = _tree.Project(Path.Combine(a, "h"), "devops", git: false);

        // Act
        ScanResult result = await ScanAsync(Scanner(new DiscoveryOptions()), a);

        // Assert
        result.Notices.Should().ContainSingle().Which.Should().Be(new ScanNotice(
            ScanNoticeKind.DuplicateName,
            "devops",
            ScanTree.Published(g) + "; " + ScanTree.Published(h)));
    }
}
