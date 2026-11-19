using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Git;
using Zyggy.Core.Registry;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Registry;

/// <summary>
/// Spec 56 slice 2 (AC-6): a name two folders derive at the same (shallowest) level is remembered as ambiguous for the node's snapshot,
/// on a <c>names</c> and a <c>full</c> scan alike; at different levels spec 45's shallowest-wins stands and the name keeps its folder.
/// </summary>
public sealed class DiscoveryScannerAmbiguousTests : IDisposable
{
    private readonly ScanTree _tree = new();
    private readonly RecordingProcessRunner _processes = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 19, 9, 0, 0, TimeSpan.Zero));
    private readonly RecordingLogger<DiscoveryScanner> _log = new();

    public void Dispose() => _tree.Dispose();

    private Task<ScanResult> ScanAsync(DiscoveryDetail detail, string root) =>
        new DiscoveryScanner(new GitClient(_processes, new GitClientOptions(), _clock), DiscoveryScannerTests.Patterns, new DiscoveryOptions(), _clock, _log)
            .ScanAsync([root], detail, TestContext.Current.CancellationToken);

    // AC-6: a same-level tie is remembered as ambiguous, whatever the detail.
    [Theory]
    [InlineData(DiscoveryDetail.Names)]
    [InlineData(DiscoveryDetail.Full)]
    public async Task ScanAsync_TwoLevelTwoSameName_NameAmbiguous(DiscoveryDetail detail)
    {
        // Arrange
        string a = _tree.Root("a");
        _tree.Project(Path.Combine(a, "g"), "devops", git: false);
        _tree.Project(Path.Combine(a, "h"), "DevOps", git: false);
        _tree.Project(a, "other", git: false);

        // Act
        ScanResult result = await ScanAsync(detail, a);

        // Assert
        result.Ambiguous.Should().BeEquivalentTo(["devops"]);
    }

    // AC-6: the tied name keeps no folder, so the node never picks one.
    [Theory]
    [InlineData(DiscoveryDetail.Names)]
    [InlineData(DiscoveryDetail.Full)]
    public async Task ScanAsync_TwoLevelTwoSameName_NoFolderForName(DiscoveryDetail detail)
    {
        // Arrange
        string a = _tree.Root("a");
        _tree.Project(Path.Combine(a, "g"), "devops", git: false);
        _tree.Project(Path.Combine(a, "h"), "devops", git: false);
        _tree.Project(a, "other", git: false);

        // Act
        ScanResult result = await ScanAsync(detail, a);

        // Assert
        result.Folders.Keys.Should().Equal("other");
    }

    // AC-6: at different levels the shallowest keeps the name and its folder; nothing is ambiguous (spec 45 Q9 stays).
    [Theory]
    [InlineData(DiscoveryDetail.Names)]
    [InlineData(DiscoveryDetail.Full)]
    public async Task ScanAsync_LevelOneAndLevelTwoSameName_ShallowestFolderNotAmbiguous(DiscoveryDetail detail)
    {
        // Arrange
        string a = _tree.Root("a");
        string shallow = _tree.Project(a, "devops", git: false);
        _tree.Project(Path.Combine(a, "group"), "DevOps", git: false);

        // Act
        ScanResult result = await ScanAsync(detail, a);

        // Assert
        (result.Ambiguous.Count, ScanTree.Published(result.Folders["devops"])).Should().Be((0, ScanTree.Published(shallow)));
    }
}
