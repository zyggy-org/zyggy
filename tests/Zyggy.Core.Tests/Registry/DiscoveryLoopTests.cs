using System.Text;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Envelope;
using Zyggy.Core.Git;
using Zyggy.Core.Health;
using Zyggy.Core.Policy;
using Zyggy.Core.Registry;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Registry;

/// <summary>Spec 14 AC-16, AC-30 (loop half): the node's discovery cycle through substitutes.</summary>
public sealed class DiscoveryLoopTests : IDisposable
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly BusRemote Remote = new("acme-org", "acme-bus", "main", new Uri("https://example.invalid/acme-org/acme-bus.git"), null);

    private readonly ScanTree _tree = new();
    private readonly TempCheckout _checkout = new(Acme, HomeLaptop);
    private readonly RecordingProcessRunner _busProcesses = new();
    private readonly IBusProvider _provider = Substitute.For<IBusProvider>();
    private readonly IPolicySource _policy = Substitute.For<IPolicySource>();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 7, 9, 0, 0, TimeSpan.Zero));
    private readonly RecordingLogger<DiscoveryLoop> _log = new();
    private readonly DiscoveryOptions _options = new();
    private readonly RegistrySnapshot _snapshot = new();
    private readonly string _root;
    private int _scans;

    public DiscoveryLoopTests()
    {
        _root = _tree.Root("src");
        _tree.Project(_root, "calizr", git: false);
        _provider.Remote.Returns(Remote);
        _provider.PullAsync(Arg.Any<CancellationToken>()).Returns(new BusSyncResult(true, string.Empty));
        _provider.PushAsync(Arg.Any<CancellationToken>()).Returns(new PushSucceeded());
        _policy.GetAsync(Arg.Any<CancellationToken>()).Returns(_ => NodePolicy.Default with { DevRoots = Roots });
    }

    private IReadOnlyList<string> Roots { get; set; } = [];

    public void Dispose()
    {
        _checkout.Dispose();
        _tree.Dispose();
    }

    private int Commits => _busProcesses.CallsOf("commit").Count();

    private DiscoveryLoop Loop(Func<string, IEnumerable<DirectoryInfo>>? listChildren = null)
    {
        Roots = [_root];
        var scanner = new DiscoveryScanner(
            new GitClient(new RecordingProcessRunner(), new GitClientOptions(), _clock),
            DiscoveryScannerTests.Patterns,
            _options,
            _clock,
            new RecordingLogger<DiscoveryScanner>(),
            root =>
            {
                Interlocked.Increment(ref _scans);
                return listChildren is null ? new DirectoryInfo(root).EnumerateDirectories() : listChildren(root);
            });
        var bus = new BusRepository(new GitClient(_busProcesses, new GitClientOptions(), _clock), _provider, new BusIdentity(Acme, HomeLaptop), _checkout.Root,
            new EnvelopeSigner(new InMemorySecretStore()), _clock, () => 0, new RecordingLogger<BusRepository>());
        var publisher = new RegistryPublisher(bus, Options.Create(_options), _clock, new RecordingLogger<RegistryPublisher>());
        var configuration = new NodeConfiguration(Acme, HomeLaptop, TestKeys.Acme1,
            new BusSettings("acme-org", "acme-bus", "main", _checkout.Root, "https://example.invalid/acme-org/acme-bus.git"),
            new PollSettings(), new SecretsSettings(SecretStoreKind.File, "/secrets"));
        return new DiscoveryLoop(scanner, _snapshot, publisher, _policy, configuration, Options.Create(_options), _clock, _log);
    }

    private async Task UntilAsync(Func<bool> condition, CancellationToken ct)
    {
        for (int i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(5, ct);
        }

        condition().Should().BeTrue("the condition should hold within the wait (scans so far: {0})", _scans);
    }

    private static async Task SettleAsync(CancellationToken ct)
    {
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(5, ct);
        }
    }

    [Fact]
    public async Task RunAsync_Schedule_ScanAtStartThenEveryInterval()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        DiscoveryLoop loop = Loop();

        // Act + Assert: each cycle settles before the clock moves on (a tick during a running cycle is skipped by design)
        Task run = loop.RunAsync(stop.Token);
        await UntilAsync(() => _scans == 1 && Commits == 1, ct);
        _clock.Advance(TimeSpan.FromMinutes(59));
        await SettleAsync(ct);
        _scans.Should().Be(1);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await UntilAsync(() => _scans == 2, ct);
        await SettleAsync(ct);
        for (int i = 0; i < 3; i++)
        {
            _clock.Advance(TimeSpan.FromHours(1));
            await UntilAsync(() => _scans == 3 + i, ct);
            await SettleAsync(ct);
        }

        _scans.Should().Be(5);
        await stop.CancelAsync();
        await run.Invoking(t => t).Should().NotThrowAsync();
    }

    [Fact]
    public async Task FirstScan_CompletesAfterSnapshotReplaced_BeforePublish()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        DiscoveryLoop loop = Loop();
        var gate = new TaskCompletionSource();
        loop.BeforeFirstPublish = _ => gate.Task;

        // Act
        Task cycle = loop.RunCycleAsync(ct);
        await loop.FirstScan.WaitAsync(TimeSpan.FromSeconds(10), ct);

        // Assert
        _snapshot.Current.Should().NotBeNull();
        _snapshot.Current!.Projects.Select(p => p.Name).Should().Equal("calizr");
        Commits.Should().Be(0);
        gate.SetResult();
        await cycle;
        Commits.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_ScanThrows_WarnOnceKeepScheduleFirstScanCompleted()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        int calls = 0;
        DiscoveryLoop loop = Loop(root => ++calls <= 2 ? throw new InvalidOperationException("boom") : new DirectoryInfo(root).EnumerateDirectories());

        // Act
        Task run = loop.RunAsync(stop.Token);
        await loop.FirstScan.WaitAsync(TimeSpan.FromSeconds(10), ct);
        _snapshot.Current.Should().BeNull();
        _clock.Advance(TimeSpan.FromHours(1));
        await UntilAsync(() => _scans == 2, ct);
        _clock.Advance(TimeSpan.FromHours(1));
        await UntilAsync(() => _snapshot.Current is not null, ct);

        // Assert
        _log.AtLevel(LogLevel.Warning).Select(e => e.Message).Should().Equal("discovery: scan failed InvalidOperationException");
        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task RunCycleAsync_PublishFails_SnapshotStillReplaced()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        _provider.PullAsync(Arg.Any<CancellationToken>()).Returns(new BusSyncResult(false, "network down"));
        DiscoveryLoop loop = Loop();

        // Act
        await loop.RunCycleAsync(ct);

        // Assert
        _snapshot.Current.Should().NotBeNull();
        Commits.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_TickWhileCycleRuns_SkippedDebugOnce()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var block = new ManualResetEventSlim(false);
        int calls = 0;
        DiscoveryLoop loop = Loop(root =>
        {
            if (Interlocked.Increment(ref calls) == 2)
            {
                block.Wait(TimeSpan.FromSeconds(30));
            }

            return new DirectoryInfo(root).EnumerateDirectories();
        });

        // Act
        Task run = loop.RunAsync(stop.Token);
        await UntilAsync(() => _scans == 1 && Commits == 1, ct);
        _clock.Advance(TimeSpan.FromHours(1));
        await UntilAsync(() => _scans == 2, ct);
        _clock.Advance(TimeSpan.FromHours(1));
        await UntilAsync(() => _log.AtLevel(LogLevel.Debug).Any(e => e.Message == "discovery: scan skipped, previous still running"), ct);
        block.Set();
        await SettleAsync(ct);

        // Assert
        _scans.Should().Be(2, "the skipped tick started no second concurrent scan");
        _log.AtLevel(LogLevel.Debug).Count(e => e.Message == "discovery: scan skipped, previous still running").Should().Be(1);
        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task RunCycleAsync_HealthFunc_UsedOnPublish()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        DiscoveryLoop loop = Loop();
        loop.Health = () => HealthLevel.Amber;

        // Act
        await loop.RunCycleAsync(ct);

        // Assert
        Encoding.UTF8.GetString(_checkout.Read(BusPaths.Registry(Acme, HomeLaptop))).Should().Contain("health: amber\n");
    }

    [Fact]
    public async Task RunAsync_SameDuplicateTwice_OneWarning_NewDuplicate_SecondWarning()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string second = _tree.Root("more");
        _tree.Project(second, "Calizr", git: false);
        DiscoveryLoop loop = Loop();
        Roots = [_root, second];

        // Act
        await loop.RunCycleAsync(ct);
        await loop.RunCycleAsync(ct);
        _tree.Project(_root, "billing", git: false);
        _tree.Project(second, "Billing", git: false);
        await loop.RunCycleAsync(ct);

        // Assert
        _log.AtLevel(LogLevel.Warning).Select(e => e.Message).Should().Equal(
            "discovery: duplicate name calizr: " + string.Join("; ", new[] { Path.Combine(_root, "calizr"), Path.Combine(second, "Calizr") }.Select(ScanTree.Published).Order(StringComparer.Ordinal)),
            "discovery: duplicate name billing: " + string.Join("; ", new[] { Path.Combine(_root, "billing"), Path.Combine(second, "Billing") }.Select(ScanTree.Published).Order(StringComparer.Ordinal)));
    }
}
