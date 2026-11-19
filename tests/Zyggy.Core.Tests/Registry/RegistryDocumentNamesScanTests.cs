using System.Text;

using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Configuration;
using Zyggy.Core.Git;
using Zyggy.Core.Registry;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Registry;

/// <summary>
/// Spec 56 slice 1 (AC-3, AC-4): a <c>detail: names</c> scan keeps each project's folder beside its projects, for the node's snapshot only;
/// the registry document built from it, and the file serialised from that, carry no path. A <c>full</c> scan's file is unchanged.
/// </summary>
public sealed class RegistryDocumentNamesScanTests : IDisposable
{
    private static readonly DateTimeOffset T = new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);

    private readonly ScanTree _tree = new();
    private readonly RecordingProcessRunner _processes = new();
    private readonly FakeTimeProvider _clock = new(T);
    private readonly RecordingLogger<DiscoveryScanner> _log = new();

    public void Dispose() => _tree.Dispose();

    // AC-3: the scan hands the folder, in its real case, to the snapshot even when the detail publishes none.
    [Fact]
    public async Task ScanAsync_NamesDetail_FolderKeptBesideProjects()
    {
        // Arrange
        string root = _tree.Root("src");
        string portal = _tree.Project(_tree.Folder(root, "Clients"), "AcmePortal", git: false);

        // Act
        ScanResult scan = await ScanAsync(root, DiscoveryDetail.Names);

        // Assert
        scan.Folders.Should().Equal(new Dictionary<string, string>(StringComparer.Ordinal) { ["acmeportal"] = Path.GetFullPath(portal) });
    }

    // AC-4: the document of a names scan whose folders are known carries no path, and its file has no `path` key.
    [Fact]
    public async Task FromScan_NamesScanWithFolders_FileHasNoPath()
    {
        // Arrange
        string root = _tree.Root("src");
        _tree.Project(_tree.Folder(root, "Clients"), "AcmePortal", git: false);
        ScanResult scan = await ScanAsync(root, DiscoveryDetail.Names);

        // Act
        string file = Encoding.UTF8.GetString(RegistryDocument.FromScan(Configuration(), scan, T, health: null).Serialize());

        // Assert
        file.Should().Contain("acmeportal").And.NotContain("path:").And.NotContain("AcmePortal").And.NotContain(_tree.Base.Replace('\\', '/'));
    }

    // AC-4: a full scan's file still publishes the path.
    [Fact]
    public async Task FromScan_FullScan_FilePublishesPath()
    {
        // Arrange
        string root = _tree.Root("src");
        string portal = _tree.Project(_tree.Folder(root, "Clients"), "AcmePortal", git: false);
        ScanResult scan = await ScanAsync(root, DiscoveryDetail.Full);

        // Act
        string file = Encoding.UTF8.GetString(RegistryDocument.FromScan(Configuration(), scan, T, health: null).Serialize());

        // Assert
        file.Should().Contain("path:").And.Contain(ScanTree.Published(portal));
    }

    private Task<ScanResult> ScanAsync(string root, DiscoveryDetail detail) =>
        new DiscoveryScanner(new GitClient(_processes, new GitClientOptions(), _clock), DiscoveryScannerTests.Patterns, new DiscoveryOptions(), _clock, _log, null)
            .ScanAsync([root], detail, TestContext.Current.CancellationToken);

    private static NodeConfiguration Configuration() =>
        new(TenantId.Parse("acme"), MachineName.Parse("work-laptop"), TestKeys.Acme1,
            new BusSettings("acme-org", "acme-bus", "main", "/bus", "https://example.invalid/acme-org/acme-bus.git"),
            new PollSettings(), new SecretsSettings(SecretStoreKind.File, "/secrets"));
}
