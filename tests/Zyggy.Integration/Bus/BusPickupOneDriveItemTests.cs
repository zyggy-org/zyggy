using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Memory;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Bus;

/// <summary>
/// Spec 41 AC-29, AC-30, AC-33 and Edge Cases row 2 against a bare bus repository, a memory repository with a bare remote (real git),
/// the instance's M365 configuration with a throw-away key, and the stub Graph handler over the in-memory OneDrive (plan 41 Step 12):
/// Central's pickup downloads a <c>via: onedrive</c> item read-only, files it exactly like a bus item and settles it in one bus commit.
/// </summary>
public sealed class BusPickupOneDriveItemTests : IAsyncLifetime
{
    private const string Item = "archive/calizr/staging-restore-notes.txt";

    private const string Sidecar = "archive/calizr/staging-restore-notes.md";

    private const string DrivePath = "Zyggy/archive/calizr/staging-restore-notes.txt";

    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName Central = MachineName.Central;

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly MachineName WorkLaptop = MachineName.Parse("work-laptop");

    private static readonly byte[] Notes = Encoding.UTF8.GetBytes("# Staging restore\n\nRestore the staging database from Sunday's snapshot, then rerun the migrations.\n");

    private readonly BusRepoFixture _bus = new(Acme);

    private readonly HookedProcessRunner _hooks = TestHost.Hooks();

    private readonly RecordingLoggerProvider _log = new();

    private readonly PickupArchiveMemory _memory = new("""{"item_drive_wait_hours": 1}""", m365: true);

    private InMemorySecretStore _store = null!;

    private ServiceProvider? _services;

    private string _project = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTimeOffset Created => DateTimeOffset.UtcNow.AddMinutes(-5);

    public async ValueTask InitializeAsync()
    {
        await _bus.InitializeAsync();
        await _bus.SeedTenantAsync(Acme, [Central, HomeLaptop, WorkLaptop], CancellationToken.None);
        await _memory.InitializeAsync();
        _store = await TestHost.StoreAsync(Acme, CancellationToken.None);
        _project = _memory.Drive.Add(StubOneDrive.RootId, "calizr");
    }

    public async ValueTask DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        await _bus.DisposeAsync();
        await _memory.DisposeAsync();
    }

    [Fact]
    public async Task RunAsync_OneDriveItem_FileOnStubDrive_DownloadedFiledPushed_ReportOneDriveLine_NoGraphWrite()
    {
        // Arrange
        _memory.Drive.Add(_project, "staging-restore-notes.txt", Notes);
        SeededJob item = await SeedOneDriveItemAsync(Notes);

        // Act
        PickupResult result = await PassAsync();

        // Assert: memory — item, sidecar and the archive add commit on the bare remote.
        result.ItemsFiled.Should().Be(1);
        (await _memory.Repo.ShowBytesAsync("acme/alice/" + Item, Ct)).Should().Equal(Notes);
        (await _memory.Repo.ShowAsync("acme/alice/" + Sidecar, Ct)).Should().Contain("source_name: restore-notes.md").And.Contain("sha256: " + Sha(Notes));
        (await _memory.Repo.LastCommitSubjectAsync(Ct)).Should().Be("archive add calizr/staging-restore-notes.txt");

        // Assert: bus — the items/ file removed and Central's report added in one commit, with the onedrive line.
        (await _bus.ExistsOnMainAsync(item.RelativePath, Ct)).Should().BeFalse();
        (await _bus.LastCommitSubjectAsync(Ct)).Should().Be("acme archive 1 central→archive");
        (await _bus.RunGitAsync(_bus.BareDir, ["log", "-1", "--format=%b", "main"], Ct)).Trim().Should().Be($"{item.RelativePath} → filed {Item}");
        (string reportPath, Model report) = await SingleReportAsync();
        report.Report!.Status.Should().Be(ReportStatus.Done);
        string memorySha = await _memory.Repo.GitAsync(_memory.Repo.BareDir, ["rev-parse", "main"], Ct);
        report.BodyText.Should().StartWith($"archived: {Item}\nsidecar: {Sidecar}\n- [stated] ")
            .And.EndWith($"\ncommit: {memorySha} pushed\nonedrive: {DrivePath} (adopted by the next offload)\n");

        // Assert: Graph — GETs only (and the one token POST), the download through the tenant's download host, the drive untouched.
        _memory.Graph.Violations.Should().BeEmpty();
        _memory.Graph.Requests.Should().NotContain(r => r.Method != HttpMethod.Get && !(r.Method == HttpMethod.Post && r.Uri.Host == "login.microsoftonline.com"));
        _memory.Graph.Requests.Should().ContainSingle(r => r.Uri.Host == StubGraphHandler.Download).Which.Headers.Should().NotContainKey("Authorization");
        _memory.Drive.Puts.Should().Be(0);
        _memory.Drive.Posts.Should().Be(0);
        _memory.Drive.File(_project, "staging-restore-notes.txt").Should().Equal(Notes);
        SecretSweep.AssertAbsent(_log.Messages);
        _log.Messages.Should().NotContain(m => m.Contains("Sunday's snapshot", StringComparison.Ordinal));
        string requests = string.Join('\n', _memory.Graph.Requests.Select(r => $"{r.Method} {Uri.UnescapeDataString(r.Uri.ToString())}"));
        TestContext.Current.TestOutputHelper?.WriteLine($"stub requests:\n{requests}\n\nreport {reportPath}:\n{await _bus.ShowAsync(reportPath, Ct)}");
    }

    [Fact]
    public async Task RunAsync_OneDriveItem_FileNeverAppears_AfterWaitHours_FailedArchiveRefusedDriveMissing_CopyStaysLine()
    {
        // Arrange: item_drive_wait_hours is 1 in archive.json; the item was created two hours ago and its file never reached OneDrive.
        SeededJob item = await SeedOneDriveItemAsync(Notes, DateTimeOffset.UtcNow.AddHours(-2));
        string before = await _memory.Repo.TreeFingerprintAsync(Ct);

        // Act
        PickupResult result = await PassAsync();

        // Assert
        result.ItemsRefused.Should().Be(1);
        (await _memory.Repo.TreeFingerprintAsync(Ct)).Should().Be(before);
        (await _bus.ExistsOnMainAsync(item.RelativePath, Ct)).Should().BeFalse();
        (string reportPath, Model report) = await SingleReportAsync();
        report.Report!.Status.Should().Be(ReportStatus.Failed);
        report.Report.Reason.Should().Be("archive_refused");
        report.BodyText.Should().Be(
            "archive: refused: drive_missing (not on OneDrive after 1 h)\n"
            + $"the OneDrive copy at {DrivePath} stays; delete it in OneDrive if wanted\n");
        (await _bus.RunGitAsync(_bus.BareDir, ["log", "-1", "--format=%b", "main"], Ct)).Trim().Should().Be($"{item.RelativePath} → refused drive_missing");
        _memory.Graph.Requests.Should().NotContain(r => r.Uri.Host == StubGraphHandler.Download);
        TestContext.Current.TestOutputHelper?.WriteLine($"report {reportPath}:\n{await _bus.ShowAsync(reportPath, Ct)}");
    }

    [Fact]
    public async Task RunAsync_OneDriveItem_FileNotThereYet_Waits_ThenAppears_FiledOnce()
    {
        // Arrange
        SeededJob item = await SeedOneDriveItemAsync(Notes);

        // Act
        PickupResult waiting = await PassAsync();
        _memory.Drive.Add(_project, "staging-restore-notes.txt", Notes);
        PickupResult notDue = await PassAsync();

        // Assert: the second pass is within the minute — no second lookup; the item stays until the next one.
        waiting.ItemsWaiting.Should().Be(1);
        notDue.ItemsWaiting.Should().Be(1);
        (await _bus.ExistsOnMainAsync(item.RelativePath, Ct)).Should().BeTrue();
        (await ReportPathsAsync()).Should().BeEmpty();
        _memory.Graph.To("root:/Zyggy/archive/calizr/staging-restore-notes\\.txt\\?").Should().ContainSingle();
        _log.Messages.Should().ContainSingle(m => m == $"pickup: item {item.Id} waits for OneDrive");
    }

    [Fact]
    public async Task RunAsync_OneDriveFileEditedBeforeFetch_DriveMismatchSha256_FileUntouchedOnStub()
    {
        // Arrange: the owner edited the file in OneDrive (same length) before Central fetched it.
        byte[] edited = [.. Notes];
        edited[2] = (byte)'s';
        string file = _memory.Drive.Add(_project, "staging-restore-notes.txt", Notes);
        _memory.Drive.Edit(file, edited);
        await SeedOneDriveItemAsync(Notes);
        string before = await _memory.Repo.TreeFingerprintAsync(Ct);

        // Act
        PickupResult result = await PassAsync();

        // Assert
        result.ItemsRefused.Should().Be(1);
        (await _memory.Repo.TreeFingerprintAsync(Ct)).Should().Be(before);
        (_, Model report) = await SingleReportAsync();
        report.BodyText.Should().Be(
            "archive: refused: drive_mismatch (sha256)\n"
            + $"the OneDrive copy at {DrivePath} stays; delete it in OneDrive if wanted\n");
        _memory.Drive.File(_project, "staging-restore-notes.txt").Should().Equal(edited);
        _memory.Drive.Puts.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_SameFileThreeRoutes_CentralBusOneDrive_ItemSidecarIndexLineCommitMessageIdentical()
    {
        // Arrange: the bus route into a second memory repository, Central's own `archive add` into a third.
        await using var busRoute = new PickupArchiveMemory();
        await busRoute.InitializeAsync();
        await using var central = new PickupArchiveMemory();
        await central.InitializeAsync();
        string source = await central.Repo.WriteSourceAsync("restore-notes.md", Notes);
        ZyggyRun add = await central.Repo.ArchiveAsync(
            Ct, null, ["add", "--project", "calizr", "--name", "Staging restore notes", "--description", "How the staging database was restored", "--file", source, "--slug", "staging-restore-notes"]);
        add.ExitCode.Should().Be(0, add.Stderr + add.Stdout);
        await _bus.SeedSignedItemAsync(HomeLaptop, Central, TestKeys.Acme1, _store, Fields(Notes, ItemRoute.Bus), Notes, Created, ct: Ct);
        await using (ServiceProvider bus = Services(busRoute))
        {
            await _bus.PullCloneAsync(Ct);
            (await bus.GetRequiredService<BusPickup>().RunAsync(Ct)).ItemsFiled.Should().Be(1);
        }

        _memory.Drive.Add(_project, "staging-restore-notes.txt", Notes);
        await SeedOneDriveItemAsync(Notes);

        // Act
        PickupResult result = await PassAsync();

        // Assert
        result.ItemsFiled.Should().Be(1);
        string oneDriveRoute = await SnapshotAsync(_memory);
        oneDriveRoute.Should().Be(await SnapshotAsync(busRoute)).And.Be(await SnapshotAsync(central));
        TestContext.Current.TestOutputHelper?.WriteLine(oneDriveRoute);
    }

    // Item bytes (base64), sidecar, inbox (index line) and the memory commit message: what AC-30 requires identical across routes.
    private static async Task<string> SnapshotAsync(PickupArchiveMemory memory) =>
        string.Join(
            "\n----\n",
            Convert.ToBase64String(await memory.Repo.ShowBytesAsync("acme/alice/" + Item, Ct)),
            await memory.Repo.ShowAsync("acme/alice/" + Sidecar, Ct),
            InboxText(memory),
            await memory.Repo.GitAsync(memory.Repo.BareDir, ["log", "-1", "--format=%B", "main"], Ct));

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static ItemFields Fields(byte[] file, ItemRoute via) => new(
        Slug.Parse("calizr"),
        Slug.Parse("staging-restore-notes"),
        "Staging restore notes",
        "How the staging database was restored",
        "restore-notes.md",
        file.Length,
        Sha(file),
        via,
        via == ItemRoute.OneDrive ? "staging-restore-notes.txt" : null);

    private static string InboxText(PickupArchiveMemory memory) => File.ReadAllText(Directory.EnumerateFiles(memory.Paths.InboxDirectory).Single());

    private Task<SeededJob> SeedOneDriveItemAsync(byte[] file, DateTimeOffset? created = null) =>
        _bus.SeedSignedItemAsync(HomeLaptop, Central, TestKeys.Acme1, _store, Fields(file, ItemRoute.OneDrive), [], created ?? Created, ct: Ct);

    private async Task<IReadOnlyList<string>> ReportPathsAsync()
    {
        string listing = await _bus.RunGitAsync(_bus.BareDir, ["ls-tree", "--name-only", "main", BusPaths.Reports(Acme, Central) + "/"], Ct);
        return [.. listing.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(p => p.EndsWith(".md", StringComparison.Ordinal))];
    }

    private async Task<(string Path, Model Report)> SingleReportAsync()
    {
        string path = (await ReportPathsAsync()).Should().ContainSingle().Subject;
        EnvelopeResult verified = await new EnvelopeSigner(_store).ParseAndVerifyAsync(await _bus.ShowBytesAsync(path, Ct), Acme, Ct);
        verified.IsAccepted.Should().BeTrue(verified.Rejection?.ToString());
        return (path, verified.Envelope!);
    }

    private ServiceProvider Services(PickupArchiveMemory memory) =>
        TestHost.Build(_bus, Central, _store, _hooks, logging: _log, extend: s =>
        {
            s.AddSingleton(TestKeys.Acme1);
            s.AddBusPickup(memory.Configuration(TestHost.NodeConfigurationFor(_bus, Central)));
            s.AddSingleton(memory.DriveOptions);
        });

    private async Task<PickupResult> PassAsync()
    {
        _services ??= Services(_memory);
        await _bus.PullCloneAsync(Ct);
        return await _services.GetRequiredService<BusPickup>().RunAsync(Ct);
    }
}
