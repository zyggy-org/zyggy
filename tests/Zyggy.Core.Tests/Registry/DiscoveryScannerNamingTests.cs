using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Git;
using Zyggy.Core.Registry;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Registry;

/// <summary>
/// Spec 45 slice 4: <c>discovery.naming</c> — <c>leaf</c> (default) names a project after its folder, <c>path</c> after the path below
/// the innermost root it was found from.
/// </summary>
public sealed class DiscoveryScannerNamingTests : IDisposable
{
    private readonly ScanTree _tree = new();
    private readonly RecordingProcessRunner _processes = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero));
    private readonly RecordingLogger<DiscoveryScanner> _log = new();

    public void Dispose() => _tree.Dispose();

    private DiscoveryScanner Scanner(DiscoveryNaming naming) =>
        new(new GitClient(_processes, new GitClientOptions(), _clock), DiscoveryScannerTests.Patterns, new DiscoveryOptions { Naming = naming }, _clock, _log, null);

    private static Task<ScanResult> ScanAsync(DiscoveryScanner scanner, params string[] roots) =>
        scanner.ScanAsync(roots, DiscoveryDetail.Full, TestContext.Current.CancellationToken);

    // AC-15: leaf stays the default.
    [Fact]
    public void Options_Default_Leaf()
    {
        new DiscoveryOptions().Naming.Should().Be(DiscoveryNaming.Leaf);
    }

    // AC-15: `path` joins the folded segments below the root; level 1 keeps its leaf name; both published.
    [Fact]
    public async Task ScanAsync_PathNaming_SegmentsJoined()
    {
        // Arrange
        string a = _tree.Root("a");
        _tree.Project(Path.Combine(a, "Common"), "devops", git: false);
        _tree.Project(a, "devops", git: false);

        // Act
        ScanResult result = await ScanAsync(Scanner(DiscoveryNaming.Path), a);

        // Assert
        result.Projects.Select(p => (p.Name, p.Path)).Should().Equal(
            ("common-devops", ScanTree.Published(Path.Combine(a, "Common", "devops"))),
            ("devops", ScanTree.Published(Path.Combine(a, "devops"))));
        result.Notices.Should().BeEmpty();
    }

    // AC-15: a project reached from `Repos` and the nested root `Repos/Common` is named from `Repos/Common`.
    [Fact]
    public async Task ScanAsync_PathNamingNestedRoot_NamedFromInnermostRoot()
    {
        // Arrange
        string repos = _tree.Root("Repos");
        string common = _tree.Folder(repos, "Common");
        _tree.Project(common, "devops", git: false);

        // Act
        ScanResult result = await ScanAsync(Scanner(DiscoveryNaming.Path), repos, common);

        // Assert
        result.Projects.Select(p => p.Name).Should().Equal("devops");
        result.Notices.Select(n => n.Kind).Should().Equal(ScanNoticeKind.RootNested);
    }

    // AC-15 with the slice review tree: leaf → `q` plus a duplicate notice; path → `q` and `g-q`, no notice.
    [Theory]
    [InlineData(DiscoveryNaming.Leaf, new[] { "q" }, 1)]
    [InlineData(DiscoveryNaming.Path, new[] { "g-q", "q" }, 0)]
    public async Task ScanAsync_SameLeafInGroup_NamingDecides(DiscoveryNaming naming, string[] names, int duplicates)
    {
        // Arrange
        string a = _tree.Root("a");
        _tree.Project(a, "q", git: false);
        _tree.Project(Path.Combine(a, "G"), "q", git: false);

        // Act
        ScanResult result = await ScanAsync(Scanner(naming), a);

        // Assert
        result.Projects.Select(p => p.Name).Should().Equal(names);
        result.Notices.Count(n => n.Kind == ScanNoticeKind.DuplicateName).Should().Be(duplicates);
    }
}
