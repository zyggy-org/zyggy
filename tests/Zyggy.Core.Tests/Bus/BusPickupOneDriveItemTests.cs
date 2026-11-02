using System.Text;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using NSubstitute;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Git;
using Zyggy.Core.M365.Graph;
using Zyggy.Core.Memory;
using Zyggy.Core.Processes;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Core.Tests.M365;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Bus;

/// <summary>
/// Spec 41 AC-29, AC-31, AC-33 (plan 41 Step 11): Central's pickup settles a <c>via: onedrive</c> item exactly like a bus item once the
/// fetcher has its bytes — the same writer, checks, commit and report, plus the <c>onedrive:</c> report line — refuses a missing or
/// mismatching drive file with <c>archive_refused</c> and the "copy stays" line, and leaves a waiting item without a report. Graph is the
/// stub handler read-only; memory and bus git are recorded.
/// </summary>
public sealed class BusPickupOneDriveItemTests : IDisposable
{
    private const string DrivePath = "Zyggy/archive/calizr/staging-restore-notes.txt";

    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName Central = MachineName.Central;

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly BusRemote Remote = new("acme-org", "acme-bus", "main", new Uri("https://example.invalid/acme-org/acme-bus.git"), null);

    private static readonly DateTimeOffset T = new(2026, 10, 31, 14, 5, 0, TimeSpan.Zero);

    private static readonly byte[] Notes = Encoding.UTF8.GetBytes("# Restore\n\nSteps for the staging restore.\n");

    private static readonly SecretPatterns Patterns = SecretPatterns.Load(Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt")).Patterns!;

    private readonly TempCheckout _checkout = new(Acme, Central);

    private readonly MemoryTree _memory = new(("profile.md", "---\nname: profile\n---\n"));

    private readonly RecordingProcessRunner _processes = new();

    private readonly IBusProvider _provider = Substitute.For<IBusProvider>();

    private readonly RecordingLogger<BusPickup> _log = new();

    private readonly GraphFixture _graph = new(retries: 0);

    private readonly StubOneDrive _drive = new();

    private readonly string _project;

    private ISecretStore? _store;

    public BusPickupOneDriveItemTests()
    {
        _provider.Remote.Returns(Remote);
        _provider.PullAsync(Arg.Any<CancellationToken>()).Returns(new BusSyncResult(true, string.Empty));
        _provider.PushAsync(Arg.Any<CancellationToken>()).Returns(new PushSucceeded());
        _graph.Clock.SetUtcNow(T);
        _graph.Stub.ReadDrive(_drive);
        _project = _drive.Add(StubOneDrive.RootId, "calizr");
    }

    public void Dispose()
    {
        _graph.Stub.Violations.Should().BeEmpty();
        _graph.Stub.Requests.Should().NotContain(r => r.Method != HttpMethod.Get && r.Uri.Host != "login.microsoftonline.com");
        _graph.Dispose();
        _checkout.Dispose();
        _memory.Dispose();
    }

    [Fact]
    public async Task RunAsync_OneDriveItemFetched_FiledLikeBusItem_ReportHasOneDriveLine()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        _drive.Add(_project, "staging-restore-notes.txt", Notes);
        (string path, EnvelopeId id) = await DropAsync(ct);

        // Act
        PickupResult result = await (await PickupAsync(ct)).RunAsync(ct);

        // Assert
        result.ItemsFiled.Should().Be(1);
        File.ReadAllBytes(_memory.Full("archive/calizr/staging-restore-notes.txt")).Should().Equal(Notes);
        _processes.Calls.Single(c => c.WorkingDirectory == _memory.Root && RecordingProcessRunner.SubCommand(c) == "commit")
            .StandardInput.Should().StartWith("archive add calizr/staging-restore-notes.txt\n\nmedia: text/markdown;");
        Model report = await SingleReportAsync(ct);
        report.Header.InReplyTo.Should().Be(id);
        report.Report!.Status.Should().Be(ReportStatus.Done);
        report.BodyText.Should().StartWith("archived: archive/calizr/staging-restore-notes.txt\nsidecar: archive/calizr/staging-restore-notes.md\n")
            .And.EndWith($"\ncommit: {Sha} pushed\nonedrive: {DrivePath} (adopted by the next offload)\n");
        BusCommit().StandardInput.Should().Be($"acme archive 1 central→archive\n\n{path} → filed archive/calizr/staging-restore-notes.txt");
        _drive.File(_project, "staging-restore-notes.txt").Should().Equal(Notes);
    }

    [Fact]
    public async Task RunAsync_DriveMissing_FailedArchiveRefused_CopyStaysLine()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (string path, _) = await DropAsync(ct);
        _graph.Clock.Advance(TimeSpan.FromHours(24));
        var memoryBefore = TreeFingerprint.Of(_memory.Root);

        // Act
        PickupResult result = await (await PickupAsync(ct)).RunAsync(ct);

        // Assert
        result.ItemsRefused.Should().Be(1);
        TreeFingerprint.Of(_memory.Root).Should().Equal(memoryBefore);
        Model report = await SingleReportAsync(ct);
        report.Report!.Status.Should().Be(ReportStatus.Failed);
        report.Report.Reason.Should().Be("archive_refused");
        report.BodyText.Should().Be(
            "archive: refused: drive_missing (not on OneDrive after 24 h)\n"
            + $"the OneDrive copy at {DrivePath} stays; delete it in OneDrive if wanted\n");
        BusCommit().StandardInput.Should().Be($"acme archive 1 central→archive\n\n{path} → refused drive_missing");
        _log.AtLevel(LogLevel.Warning).Should().ContainSingle().Which.Message.Should().StartWith("pickup: item ").And.Contain("refused: drive_missing");
    }

    [Fact]
    public async Task RunAsync_DriveNameExtensionDiffersFromSniff_DriveMismatchType()
    {
        // Arrange: the pointer names a PDF, the drive file at that name is Markdown text.
        var ct = TestContext.Current.CancellationToken;
        _drive.Add(_project, "staging-restore-notes.pdf", Notes);
        await DropAsync(ct, driveName: "staging-restore-notes.pdf");
        var memoryBefore = TreeFingerprint.Of(_memory.Root);

        // Act
        PickupResult result = await (await PickupAsync(ct)).RunAsync(ct);

        // Assert
        result.ItemsRefused.Should().Be(1);
        TreeFingerprint.Of(_memory.Root).Should().Equal(memoryBefore);
        (await SingleReportAsync(ct)).BodyText.Should().StartWith("archive: refused: drive_mismatch (type)\nthe OneDrive copy at Zyggy/archive/calizr/staging-restore-notes.pdf stays;");
    }

    [Fact]
    public async Task RunAsync_Waiting_NoReport_ItemStays_ItemsWaitingCounted()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (string path, _) = await DropAsync(ct);
        BusPickup pickup = await PickupAsync(ct);

        // Act
        PickupResult first = await pickup.RunAsync(ct);
        PickupResult second = await pickup.RunAsync(ct);

        // Assert
        first.ItemsWaiting.Should().Be(1);
        second.ItemsWaiting.Should().Be(1);
        first.Archive.Should().BeNull();
        _checkout.Exists(path).Should().BeTrue();
        _checkout.FilesUnder(BusPaths.Reports(Acme, Central)).Should().BeEmpty();
        _processes.Calls.Should().NotContain(c => c.WorkingDirectory == _memory.Root);
        _graph.Stub.To("root:/Zyggy/archive/calizr/staging-restore-notes\\.txt\\?").Should().ContainSingle();
        _log.AtLevel(LogLevel.Information).Select(e => e.Message).Should().ContainSingle(m => m.EndsWith("waits for OneDrive", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_NoFetcher_WaitsWithErrorNotConfiguredOnce()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropAsync(ct);
        BusPickup pickup = await PickupAsync(ct, fetcher: false);

        // Act
        PickupResult result = await pickup.RunAsync(ct);
        await pickup.RunAsync(ct);

        // Assert
        result.ItemsWaiting.Should().Be(1);
        _log.AtLevel(LogLevel.Error).Should().ContainSingle().Which.Message.Should().StartWith("pickup: OneDrive fetch not configured: ");
    }

    [Fact]
    public void RunAsync_NoDriveWriterResolved()
    {
        // Arrange
        var services = new ServiceCollection()
            .AddSingleton<TimeProvider>(_graph.Clock)
            .AddSingleton<IProcessRunner>(_processes)
            .AddSingleton(new EnvelopeSigner(new InMemorySecretStore()))
            .AddSingleton(sp => new BusRepository(
                new GitClient(_processes, new GitClientOptions(), _graph.Clock), _provider, new BusIdentity(Acme, Central), _checkout.Root,
                sp.GetRequiredService<EnvelopeSigner>(), _graph.Clock, () => 0.5, new RecordingLogger<BusRepository>()));
        var environment = new Dictionary<string, string?> { ["HOME"] = Path.GetTempPath() };
        var options = new ArchiveOptions();
        PickupConfigurationResult configuration = PickupConfigurationResult.For(_memory.Paths, TimeZoneInfo.Utc, Patterns) with { Archive = options, Environment = environment };

        // Act
        services.AddBusPickup(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();
        BusPickup pickup = provider.GetRequiredService<BusPickup>();

        // Assert
        pickup.Should().NotBeNull();
        provider.GetService<ItemDriveOptions>().Should().NotBeNull();
        services.Should().NotContain(d => d.ServiceType == typeof(IGraphDriveWriter) || d.ServiceType == typeof(GraphDriveWriter));
        provider.GetService<IGraphDriveWriter>().Should().BeNull();
    }

    private ProcessSpec BusCommit() =>
        _processes.CallsOf("commit").Should().ContainSingle(c => c.WorkingDirectory == _checkout.Root).Subject;

    private async Task<Model> SingleReportAsync(CancellationToken ct)
    {
        var signer = new EnvelopeSigner(await StoreAsync(ct));
        string path = _checkout.FilesUnder(BusPaths.Reports(Acme, Central)).Should().ContainSingle().Subject;
        EnvelopeResult verified = await signer.ParseAndVerifyAsync(_checkout.Read(path), Acme, ct);
        verified.IsAccepted.Should().BeTrue(verified.Rejection?.ToString());
        return verified.Envelope!;
    }

    private async Task<ISecretStore> StoreAsync(CancellationToken ct) => _store ??= await TestKeys.StoreWithAsync(TestKeys.Acme1, ct);

    private async Task<(string Path, EnvelopeId Id)> DropAsync(CancellationToken ct, string driveName = "staging-restore-notes.txt")
    {
        var id = EnvelopeId.New(_graph.Clock.GetUtcNow());
        _graph.Clock.Advance(TimeSpan.FromMilliseconds(1));
        byte[] bytes = await TestEnvelopes.SignedOneDriveItemBytesAsync(HomeLaptop, await StoreAsync(ct), TestKeys.Acme1, Notes, T, ct, id, driveName);
        string path = BusPaths.EnvelopeFile(BusPaths.Items(Acme, HomeLaptop), id);
        _checkout.Drop(path, bytes);
        return (path, id);
    }

    private async Task<BusPickup> PickupAsync(CancellationToken ct, bool fetcher = true)
    {
        ISecretStore store = await StoreAsync(ct);
        var git = new GitClient(_processes, new GitClientOptions { Identity = TestGitIdentities.Central }, _graph.Clock);
        var signer = new EnvelopeSigner(store);
        var bus = new BusRepository(git, _provider, new BusIdentity(Acme, Central), _checkout.Root, signer, _graph.Clock, () => 0.5, new RecordingLogger<BusRepository>());
        var options = new ArchiveOptions();
        var environment = new Dictionary<string, string?> { ["HOME"] = Path.GetTempPath() };
        var publisher = new MemoryPublisher(new GitClient(_processes, new GitClientOptions(), _graph.Clock));
        var service = new ArchiveService(_memory.Paths, TimeZoneInfo.Utc, _graph.Clock, Patterns, options, SourceDenyList.Build(environment, options, _memory.Paths), publisher, _memory.Root);
        return new BusPickup(
            bus,
            signer,
            new PickupInbox(_memory.Paths, TimeZoneInfo.Utc, _graph.Clock),
            Patterns,
            _log,
            TestKeys.Acme1,
            new ItemFiler(service, options),
            null,
            fetcher ? new ItemDriveFetcher(() => (_graph.Reader, null), options, _graph.Clock, _log) : null);
    }
}
