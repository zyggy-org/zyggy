using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Envelope;
using Zyggy.Core.Memory;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Memory;

/// <summary>
/// Spec 41 AC-42 (plan 41 Step 12): Central's pickup files items into the spec 38 harness's memory repository (real git, the in-memory
/// OneDrive behind the stub Graph handler), a dream's index line names them, and the next <c>zyggy memory archive offload</c> adopts the
/// OneDrive copy of an item that came through the OneDrive route — no upload, the sidecar gains the offload block — while an Office and a
/// PNG item that came through the bus are uploaded normally.
/// </summary>
public sealed class ArchiveOffloadItemAdoptionTests : IAsyncLifetime
{
    private const string TextItem = "acme/alice/archive/calizr/staging-restore-notes.txt";

    private const string TextSidecar = "acme/alice/archive/calizr/staging-restore-notes.md";

    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName Central = MachineName.Central;

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly MachineName WorkLaptop = MachineName.Parse("work-laptop");

    private readonly OffloadHarness _h = new();

    private readonly BusRepoFixture _bus = new(Acme);

    private readonly HookedProcessRunner _hooks = TestHost.Hooks();

    private InMemorySecretStore _store = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Bytes with an independent QuickXorHash vector (quickxor-vectors.tsv), so the stub OneDrive reports their hash for adoption.
    private static byte[] Quote => Encoding.UTF8.GetBytes("Quote for the Zyggy roof repair.\nTotal 1 234,00 EUR, valid 30 days.\n");

    private static byte[] Docx => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "golden", "office", "sample.docx"));

    private static byte[] Png => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "archive-indexed", "acme", "alice", "archive", "zyggy", "screenshot.png"));

    public async ValueTask InitializeAsync()
    {
        // The harness seeds two indexed spec-37 items; this class replaces them by what the pickup files.
        await _h.InitializeAsync();
        await _h.Repo.GitAsync(_h.Repo.CloneDir, ["rm", "-q", "-r", "--", "acme/alice/archive/zyggy"], Ct);
        await _h.Repo.GitAsync(_h.Repo.CloneDir, ["commit", "-q", "-m", "clear seeded items"], Ct);
        await _h.Repo.GitAsync(_h.Repo.CloneDir, ["push", "-q", "origin", "HEAD:main"], Ct);
        await _bus.InitializeAsync();
        await _bus.SeedTenantAsync(Acme, [Central, HomeLaptop, WorkLaptop], CancellationToken.None);
        _store = await TestHost.StoreAsync(Acme, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await _bus.DisposeAsync();
        await _h.DisposeAsync();
    }

    [Fact]
    public async Task Offload_ItemFiledThroughOneDriveRoute_Indexed_ExistingFileAdopted_NoPut_SidecarOffloadBlock()
    {
        // Arrange: home-laptop's OneDrive client uploaded the file; the pickup files it read-only; a dream indexes it.
        string project = _h.Drive.Add(StubOneDrive.RootId, "calizr");
        string driveFile = _h.Drive.Add(project, "staging-restore-notes.txt", Quote);
        await _bus.SeedSignedItemAsync(HomeLaptop, Central, TestKeys.Acme1, _store, Fields(Quote, "staging-restore-notes", "quote.txt", ItemRoute.OneDrive), [], DateTimeOffset.UtcNow, ct: Ct);
        (await PickUpAsync()).ItemsFiled.Should().Be(1);
        _h.Drive.Puts.Should().Be(0);
        _h.Drive.Posts.Should().Be(0);
        await IndexAsync("staging-restore-notes.txt");
        int before = await _h.Repo.CommitCountAsync(Ct);

        // Act
        var (exit, console) = await _h.RunInProcessAsync();

        // Assert
        exit.Should().Be(0, console.Stderr);
        _h.Drive.Puts.Should().Be(0, "the OneDrive copy is adopted, not uploaded again");
        _h.Drive.Posts.Should().Be(0);
        (await _h.Repo.CommitCountAsync(Ct)).Should().Be(before + 1);
        (await _h.Repo.LastCommitSubjectAsync(Ct)).Should().Be("archive offload calizr/staging-restore-notes.txt");
        (await _h.Repo.GitAsync(_h.Repo.BareDir, ["ls-tree", "-r", "--name-only", "main"], Ct)).Split('\n').Should().NotContain(TextItem).And.Contain(TextSidecar);
        (await _h.Repo.ShowAsync(TextSidecar, Ct)).Should().Contain("drive_item_id: " + driveFile)
            .And.Contain("drive_path: Zyggy/archive/calizr/staging-restore-notes.txt")
            .And.Contain("drive_quickxor: jAt95bF64I8MSmyO3joPMtROLIU=");
    }

    [Fact]
    public async Task Offload_DocxAndPngFiledThroughBusRoute_UploadedNormally()
    {
        // Arrange: work-laptop sent both through the bus; nothing of them is on OneDrive yet.
        await _bus.SeedSignedItemAsync(WorkLaptop, Central, TestKeys.Acme1, _store, Fields(Docx, "roof-offer", "roof-offer.docx", ItemRoute.Bus), Docx, DateTimeOffset.UtcNow, ct: Ct);
        await _bus.SeedSignedItemAsync(WorkLaptop, Central, TestKeys.Acme1, _store, Fields(Png, "roof-photo", "roof.png", ItemRoute.Bus), Png, DateTimeOffset.UtcNow.AddSeconds(1), ct: Ct);
        (await PickUpAsync()).ItemsFiled.Should().Be(2);
        await IndexAsync("roof-offer.docx", "roof-photo.png");

        // Act
        var (exit, console) = await _h.RunInProcessAsync();

        // Assert
        exit.Should().Be(0, console.Stderr);
        _h.Drive.Posts.Should().Be(1, "the project folder is created once");
        _h.Drive.Puts.Should().Be(2);
        string folder = _h.Drive.ChildId(StubOneDrive.RootId, "calizr")!;
        _h.Drive.File(folder, "roof-offer.docx").Should().Equal(Docx);
        _h.Drive.File(folder, "roof-photo.png").Should().Equal(Png);
        (await _h.Repo.GitAsync(_h.Repo.BareDir, ["log", "-2", "--format=%s", "main"], Ct)).Split('\n')
            .Should().BeEquivalentTo("archive offload calizr/roof-offer.docx", "archive offload calizr/roof-photo.png");
    }

    private static ItemFields Fields(byte[] file, string slug, string sourceName, ItemRoute via) => new(
        Slug.Parse("calizr"),
        Slug.Parse(slug),
        "Roof " + slug.Replace('-', ' '),
        "Filed from a laptop for the offload adoption test",
        sourceName,
        file.Length,
        Convert.ToHexStringLower(SHA256.HashData(file)),
        via,
        via == ItemRoute.OneDrive ? slug + Path.GetExtension(sourceName) : null);

    // A dream's work in short: the project memory file names each item's path (what makes an item "indexed", spec 38 AC-1).
    private async Task IndexAsync(params string[] files)
    {
        string path = Path.Combine(_h.Repo.PrincipalDir, "business", "areas", "calizr.md");
        var text = new StringBuilder("---\nname: calizr\ndescription: Calizr, the calendar project\nupdated: 2026-10-31\n---\n");
        foreach (string file in files)
        {
            text.Append("- [stated] 2026-10-31 (project:calizr): Archived at archive/calizr/").Append(file).Append('\n');
        }

        await File.WriteAllTextAsync(path, text.ToString(), new UTF8Encoding(false), Ct);
        await _h.Repo.GitAsync(_h.Repo.CloneDir, ["add", "--", "acme/alice/business/areas/calizr.md"], Ct);
        await _h.Repo.GitAsync(_h.Repo.CloneDir, ["commit", "-q", "-m", "dream: index calizr"], Ct);
        await _h.Repo.GitAsync(_h.Repo.CloneDir, ["push", "-q", "origin", "HEAD:main"], Ct);
    }

    private async Task<PickupResult> PickUpAsync()
    {
        NodeConfiguration node = TestHost.NodeConfigurationFor(_bus, Central);
        PickupConfigurationResult configuration = PickupConfiguration.Load(_h.Env(), node);
        configuration.Error.Should().BeNull();
        configuration.ArchiveError.Should().BeNull();
        await using ServiceProvider services = TestHost.Build(_bus, Central, _store, _hooks, extend: s =>
        {
            s.AddSingleton(TestKeys.Acme1);
            s.AddBusPickup(configuration);
            s.AddSingleton(new ItemDriveOptions { GraphHandlerFactory = () => _h.Graph, CheckKeyOwnership = OperatingSystem.IsLinux() });
        });
        await _bus.PullCloneAsync(Ct);
        return await services.GetRequiredService<BusPickup>().RunAsync(Ct);
    }
}
