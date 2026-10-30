using System.Text;

using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Git;
using Zyggy.Core.Memory;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Bus;

/// <summary>
/// Spec 13 AC-14, AC-17 (§5 item 2 as amended by A13-7): Central's listener runs the pickup after the job pass of every
/// successful pull and never otherwise. Every archive <c>mv</c> is scripted to fail, so the context stays due and the number of
/// archive <c>mv</c> calls is the number of pickup passes.
/// </summary>
public sealed class BusListenerPickupTests : IAsyncLifetime
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName Central = MachineName.Central;

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly BusRemote Remote = new("acme-org", "acme-bus", "main", new Uri("https://example.invalid/acme-org/acme-bus.git"), null);

    private static readonly DateTimeOffset T = new(2026, 10, 29, 10, 0, 0, TimeSpan.Zero);

    private readonly TempCheckout _checkout = new(Acme, Central);

    private readonly MemoryTree _memory = new(("profile.md", "---\nname: profile\n---\n"));

    private readonly RecordingProcessRunner _processes = new();

    private readonly IBusProvider _provider = Substitute.For<IBusProvider>();

    private readonly FakeTimeProvider _clock = new(T);

    private readonly List<Model> _handled = [];

    private InMemorySecretStore _store = null!;

    private bool _branchMoved;

    public async ValueTask InitializeAsync()
    {
        _store = await TestKeys.StoreWithBothAsync(CancellationToken.None);
        _provider.Remote.Returns(Remote);
        _provider.PullAsync(Arg.Any<CancellationToken>()).Returns(new BusSyncResult(true, string.Empty));
        _provider.PushAsync(Arg.Any<CancellationToken>()).Returns(new PushSucceeded());
        _provider.ProbeHeadAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new Unchanged(4999));
        _processes.Hook = spec =>
        {
            if (RecordingProcessRunner.SubCommand(spec) == "mv" && spec.Arguments.Any(a => a.Contains("/archive/", StringComparison.Ordinal)))
            {
                return RecordingProcessRunner.Fail(128, "fatal: scripted archive failure");
            }

            return _branchMoved && spec.Arguments.Contains("origin/main") ? RecordingProcessRunner.Ok("abcdef0123456789abcdef0123456789abcdef01\n") : null;
        };
    }

    public ValueTask DisposeAsync()
    {
        _checkout.Dispose();
        _memory.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task StartAsync_PullSucceeds_PickupRunsOnceAfterTheJobPass()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropContextAsync(ct);
        await DropJobAsync(ct);

        // Act
        await Listener().StartAsync(ct);

        // Assert
        ArchiveMoves().Should().Be(1);
        var moves = _processes.CallsOf("mv").ToList();
        moves.FindIndex(c => c.Arguments.Any(a => a.Contains("/jobs/claimed/", StringComparison.Ordinal)))
            .Should().BeLessThan(moves.FindIndex(c => c.Arguments.Any(a => a.Contains("/archive/", StringComparison.Ordinal))));
    }

    [Fact]
    public async Task StartAsync_PullFails_NoPickup()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropContextAsync(ct);
        _provider.PullAsync(Arg.Any<CancellationToken>()).Returns(new BusSyncResult(false, "could not resolve host"));

        // Act
        await Listener().StartAsync(ct);

        // Assert
        ArchiveMoves().Should().Be(0);
        Directory.Exists(_memory.Paths.InboxDirectory).Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_ChangedTickPullSucceeds_PickupRunsOnce()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        _provider.ProbeHeadAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new Changed("W/\"1\"", 4999), new Unchanged(4999));
        BusListener listener = Listener();
        await listener.StartAsync(ct);
        await DropContextAsync(ct);

        // Act
        await RunTicksAsync(listener, 2, ct);

        // Assert
        ArchiveMoves().Should().Be(1, "the start-up pass saw nothing; the Changed tick's pass saw the context; the Unchanged tick ran none");
    }

    [Fact]
    public async Task RunAsync_UnchangedTick_NoPickup()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        BusListener listener = Listener();
        await listener.StartAsync(ct);
        await DropContextAsync(ct);

        // Act
        await RunTicksAsync(listener, 3, ct);

        // Assert
        ArchiveMoves().Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_ChangedTickPullFails_NoPickup()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        _provider.ProbeHeadAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new Changed("W/\"1\"", 4999), new Unchanged(4999));
        BusListener listener = Listener();
        await listener.StartAsync(ct);
        await DropContextAsync(ct);
        _provider.PullAsync(Arg.Any<CancellationToken>()).Returns(new BusSyncResult(false, "could not resolve host"));

        // Act
        await RunTicksAsync(listener, 2, ct);

        // Assert
        ArchiveMoves().Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_SafetyFetchMoved_PickupRunsOnce()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        BusListener listener = Listener();
        await listener.StartAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task run = listener.RunAsync(PollMode.Active, cts.Token);
        await WaitForProbesAsync(1, ct, run);
        await DropContextAsync(ct);
        _branchMoved = true;

        // Act
        _clock.SetUtcNow(T.AddMinutes(15));
        await WaitUntilAsync(() => ArchiveMoves() >= 1, ct, run);
        await Task.Delay(50, ct);
        await cts.CancelAsync();
        await run;

        // Assert
        ArchiveMoves().Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_SafetyFetchMovedAndChangedSameTick_OnePullOnePickup()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        _provider.ProbeHeadAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new Unchanged(4999), new Changed("etag-2", 4999), new Unchanged(4999));
        BusListener listener = Listener();
        await listener.StartAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task run = listener.RunAsync(PollMode.Active, cts.Token);
        await WaitForProbesAsync(1, ct, run);
        await DropContextAsync(ct);
        _branchMoved = true;

        // Act
        _clock.SetUtcNow(T.AddMinutes(15));
        await WaitForProbesAsync(2, ct, run);
        await WaitUntilAsync(() => ArchiveMoves() >= 1, ct, run);
        await Task.Delay(50, ct);
        await cts.CancelAsync();
        await run;

        // Assert
        ArchiveMoves().Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_IdleToActiveWithoutPull_NoPickup()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var inside = false;
        BusListener listener = Listener();
        await listener.StartAsync(ct);
        await DropContextAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Act
        Task run = listener.RunAsync(_ => inside ? PollMode.Active : PollMode.Idle, cts.Token);
        await WaitForProbesAsync(1, ct, run);
        await Task.Delay(20, ct);
        inside = true;
        _clock.SetUtcNow(listener.State.NextDueUtc);
        await WaitForProbesAsync(2, ct, run);
        await Task.Delay(50, ct);
        await cts.CancelAsync();
        await run;

        // Assert
        ArchiveMoves().Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_PickupPassFails_LoopKeepsPollingAndClaimingJobs()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        _provider.ProbeHeadAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new Changed("W/\"1\"", 4999), new Changed("W/\"2\"", 4999), new Unchanged(4999));
        await DropContextAsync(ct);
        BusListener listener = Listener();
        await listener.StartAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task run = listener.RunAsync(PollMode.Active, cts.Token);
        await WaitForProbesAsync(1, ct, run);
        await DropJobAsync(ct);

        // Act
        await Task.Delay(20, ct);
        _clock.SetUtcNow(listener.State.NextDueUtc);
        await WaitUntilAsync(() => _handled.Count == 1, ct, run);
        await listener.DrainAsync(ct);
        await cts.CancelAsync();
        await run;

        // Assert
        ArchiveMoves().Should().BeGreaterThanOrEqualTo(2, "the start-up pass and the Changed ticks each tried and failed to archive");
        _handled.Should().ContainSingle();
    }

    private int ArchiveMoves() => _processes.CallsOf("mv").Count(c => c.Arguments.Any(a => a.Contains("/archive/", StringComparison.Ordinal)));

    private BusListener Listener()
    {
        var git = new GitClient(_processes, new GitClientOptions { Identity = new GitIdentity("zyggy (central)", "central@zyggy.org") }, _clock);
        var signer = new EnvelopeSigner(_store);
        var repository = new BusRepository(git, _provider, new BusIdentity(Acme, Central), _checkout.Root, signer, _clock, () => 0, new RecordingLogger<BusRepository>());
        SecretPatterns patterns = SecretPatterns.Load(Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt")).Patterns!;
        var pickup = new BusPickup(repository, signer, new PickupInbox(_memory.Paths, TimeZoneInfo.Utc, _clock), patterns, new RecordingLogger<BusPickup>());
        JobHandler handler = (job, _) =>
        {
            _handled.Add(job);
            return Task.FromResult(new JobReply(ReportStatus.Done, null, Encoding.UTF8.GetBytes("ok\n"), T, T.AddSeconds(1)));
        };
        return new BusListener(_provider, repository, PollOptions.Default, TestKeys.Acme1, _clock, new Random(1), handler, new RecordingLogger<BusListener>(), null, pickup);
    }

    private async Task DropContextAsync(CancellationToken ct)
    {
        var id = EnvelopeId.New(_clock.GetUtcNow());
        byte[] bytes = await TestEnvelopes.SignedContextBytesAsync(HomeLaptop, Central, _store, TestKeys.Acme1, "- a fact\n", T, ct, id: id);
        _checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Context(Acme, HomeLaptop), id), bytes);
    }

    private async Task DropJobAsync(CancellationToken ct)
    {
        Model job = await TestEnvelopes.SignedJobAsync(HomeLaptop, Central, _store, TestKeys.Acme1, "probe\n", ct);
        _checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Jobs(Acme, Central), job.Header.Id), EnvelopeWriter.Serialize(job));
    }

    private async Task RunTicksAsync(BusListener listener, int ticks, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task run = listener.RunAsync(PollMode.Active, cts.Token);
        for (int i = 1; i <= ticks; i++)
        {
            await WaitForProbesAsync(i, ct, run);
            if (i < ticks)
            {
                await Task.Delay(20, ct);
                _clock.SetUtcNow(listener.State.NextDueUtc);
            }
        }

        await Task.Delay(50, ct);
        await cts.CancelAsync();
        await run;
    }

    private async Task WaitForProbesAsync(int count, CancellationToken ct, Task? run = null)
    {
        for (int i = 0; i < 500; i++)
        {
            if (run is { IsFaulted: true })
            {
                await run;
            }

            if (_provider.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IBusProvider.ProbeHeadAsync)) >= count)
            {
                return;
            }

            await Task.Delay(10, ct);
        }

        throw new TimeoutException("the listener did not probe in time");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct, Task? run = null)
    {
        for (int i = 0; i < 500 && !condition(); i++)
        {
            if (run is { IsFaulted: true })
            {
                await run;
            }

            await Task.Delay(10, ct);
        }
    }
}
