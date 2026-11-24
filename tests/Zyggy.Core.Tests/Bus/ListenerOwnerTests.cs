using System.Text;

using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Git;
using Zyggy.Core.Processes;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Bus;

/// <summary>
/// Spec 61 slice 7, AC-21 and AC-22 (unit half): the listener writes who holds the listen lock, a refused start leaves its record and the
/// refusal sentence, and <see cref="ListenerInspection"/> tells a live, stray, stale or other-version listener apart.
/// </summary>
public sealed class ListenerOwnerTests : IAsyncLifetime
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly DateTimeOffset T = new(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset ProcessStart = T.AddMinutes(-3);

    private readonly TempCheckout _checkout = new(Acme, HomeLaptop);

    private readonly RecordingProcessRunner _git = new();

    private readonly IBusProvider _provider = Substitute.For<IBusProvider>();

    private readonly IProcessTable _table = Substitute.For<IProcessTable>();

    private readonly FakeTimeProvider _clock = new(T);

    private InMemorySecretStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = await TestKeys.StoreWithBothAsync(CancellationToken.None);
        Directory.CreateDirectory(Path.Combine(_checkout.Root, ".git"));
        _provider.Remote.Returns(new BusRemote("acme-org", "acme-bus", "main", new Uri("https://example.invalid/acme-org/acme-bus.git"), null));
        _provider.PullAsync(Arg.Any<CancellationToken>()).Returns(new BusSyncResult(true, string.Empty));
        _provider.PushAsync(Arg.Any<CancellationToken>()).Returns(new PushSucceeded());
        _table.Self().Returns(new ProcessFacts(4242, "zyggy-node", ProcessStart, true));
    }

    public ValueTask DisposeAsync()
    {
        _checkout.Dispose();
        return ValueTask.CompletedTask;
    }

    // AC-21: the owner record
    [Fact]
    public async Task StartAsync_LockTaken_WritesOwnerRecordPidStartVersionStarted()
    {
        // Act
        using BusListener listener = Listener();
        await listener.StartAsync(TestContext.Current.CancellationToken);

        // Assert
        ListenerOwner.ReadOwner(_checkout.Root).Should().Be(new ListenerRecord(4242, ProcessStart, "0.30.1", T));
    }

    // Hardening (slice 8 verify): the owner record a killed listener left is kept as the previous listener, so the leftover model
    // processes are matched to it; the new record replaces it on disk.
    [Fact]
    public async Task StartAsync_KilledListenerLeftItsRecord_PreviousIsThatRecord()
    {
        // Arrange
        var killed = new ListenerRecord(1111, ProcessStart.AddHours(-2), "0.30.1", T.AddHours(-2));
        ListenerOwner.WriteOwner(_checkout.Root, killed);
        using BusListener listener = Listener();

        // Act
        await listener.StartAsync(TestContext.Current.CancellationToken);

        // Assert
        (listener.Owner!.Previous, ListenerOwner.ReadOwner(_checkout.Root)!.Pid).Should().Be((killed, 4242));
    }

    // AC-21: the refusal names the holder and leaves its record
    [Fact]
    public async Task StartAsync_LockHeld_RefusalDetailNamesHolderAndRefusedRecordWritten()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using BusListener first = Listener();
        (await first.StartAsync(ct)).Should().Be(BusStartupResult.Clean);
        _clock.Advance(TimeSpan.FromMinutes(5));
        using BusListener second = Listener();

        // Act
        BusStartupResult result = await second.StartAsync(ct);

        // Assert
        result.Should().Be(BusStartupResult.ListenerRunning);
        second.StartupDetail.Should().Be("listener already running (pid 4242 since 2026-11-24T10:00:00Z, zyggy-node 0.30.1) — stop it with: zyggy node stop");
        ListenerOwner.ReadRefused(_checkout.Root).Should().Be(new RefusedStart(T.AddMinutes(5), 4242));
    }

    // AC-21: the refused start does not replace or remove the holder's record
    [Fact]
    public async Task Dispose_RefusedListener_HolderRecordKept()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using BusListener first = Listener();
        await first.StartAsync(ct);
        BusListener second = Listener();
        await second.StartAsync(ct);

        // Act
        second.Dispose();

        // Assert
        ListenerOwner.ReadOwner(_checkout.Root).Should().NotBeNull();
    }

    // A clean stop removes the holder's own record
    [Fact]
    public async Task Dispose_Holder_OwnerRecordRemoved()
    {
        // Arrange
        BusListener listener = Listener();
        await listener.StartAsync(TestContext.Current.CancellationToken);

        // Act
        listener.Dispose();

        // Assert
        ListenerOwner.ReadOwner(_checkout.Root).Should().BeNull();
    }

    // AC-22 unit: live holder
    [Fact]
    public void Inspect_HolderRuns_Alive()
    {
        // Arrange
        ListenerOwner.WriteOwner(_checkout.Root, new ListenerRecord(4242, ProcessStart, "0.30.1", T));
        _table.Find(4242).Returns(new ProcessFacts(4242, "zyggy-node", ProcessStart, true));

        // Act
        ListenerFindings findings = ListenerInspection.Inspect(_checkout.Root, _table, "0.30.1");

        // Assert
        findings.Should().Be(new ListenerFindings(new ListenerRecord(4242, ProcessStart, "0.30.1", T), true, null, false, false));
    }

    // AC-22 unit: stray — a start refused after the holder started, holder alive
    [Fact]
    public void Inspect_RefusedAfterHolderStartedHolderAlive_Stray()
    {
        // Arrange
        ListenerOwner.WriteOwner(_checkout.Root, new ListenerRecord(4242, ProcessStart, "0.30.1", T));
        ListenerOwner.WriteRefused(_checkout.Root, new RefusedStart(T.AddHours(1), 4242));
        _table.Find(4242).Returns(new ProcessFacts(4242, "zyggy-node", ProcessStart, true));

        // Act
        ListenerFindings findings = ListenerInspection.Inspect(_checkout.Root, _table, "0.30.1");

        // Assert
        findings.Stray.Should().BeTrue();
    }

    // AC-22 unit: a refusal older than the holder's start is history, not a stray
    [Fact]
    public void Inspect_RefusedBeforeHolderStarted_NotStray()
    {
        // Arrange
        ListenerOwner.WriteOwner(_checkout.Root, new ListenerRecord(4242, ProcessStart, "0.30.1", T));
        ListenerOwner.WriteRefused(_checkout.Root, new RefusedStart(T.AddMinutes(-1), 1111));
        _table.Find(4242).Returns(new ProcessFacts(4242, "zyggy-node", ProcessStart, true));

        // Act
        ListenerFindings findings = ListenerInspection.Inspect(_checkout.Root, _table, "0.30.1");

        // Assert
        findings.Stray.Should().BeFalse();
    }

    // AC-22 unit: the holder runs another version than the one installed
    [Fact]
    public void Inspect_HolderOtherVersion_VersionDiffers()
    {
        // Arrange
        ListenerOwner.WriteOwner(_checkout.Root, new ListenerRecord(4242, ProcessStart, "0.30.0", T));
        _table.Find(4242).Returns(new ProcessFacts(4242, "zyggy-node", ProcessStart, true));

        // Act
        ListenerFindings findings = ListenerInspection.Inspect(_checkout.Root, _table, "0.30.1");

        // Assert
        findings.VersionDiffers.Should().BeTrue();
    }

    // AC-22 unit: stale — the pid runs no more, or runs another process start (reused pid)
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Inspect_PidGoneOrReused_NotAliveNoStrayNoVersionLine(bool reused)
    {
        // Arrange
        ListenerOwner.WriteOwner(_checkout.Root, new ListenerRecord(4242, ProcessStart, "0.30.0", T));
        ListenerOwner.WriteRefused(_checkout.Root, new RefusedStart(T.AddHours(1), 4242));
        _table.Find(4242).Returns(reused ? new ProcessFacts(4242, "notepad", T.AddHours(2), true) : null);

        // Act
        ListenerFindings findings = ListenerInspection.Inspect(_checkout.Root, _table, "0.30.1");

        // Assert
        findings.Should().Match<ListenerFindings>(f => f.Owner != null && !f.Alive && !f.Stray && !f.VersionDiffers);
    }

    private BusListener Listener()
    {
        var git = new GitClient(_git, new GitClientOptions { Identity = new GitIdentity("zyggy (home-laptop)", "home-laptop@zyggy.org") }, _clock);
        var repository = new BusRepository(git, _provider, new BusIdentity(Acme, HomeLaptop), _checkout.Root, new EnvelopeSigner(_store), _clock, () => 0, new RecordingLogger<BusRepository>());
        return new BusListener(_provider, repository, PollOptions.Default, TestKeys.Acme1, _clock, new Random(1), (_, _) => Task.FromResult(new JobReply(ReportStatus.Done, null, Encoding.UTF8.GetBytes("ok\n"), T, T)), new RecordingLogger<BusListener>())
        {
            Owner = new ListenerOwner(_checkout.Root, _table, "0.30.1", _clock),
        };
    }
}
