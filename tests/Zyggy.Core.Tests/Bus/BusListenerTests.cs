using System.Text;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Git;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Bus;

/// <summary>Spec 04 Step 9: the listen loop over substitutes and a fake clock.</summary>
public sealed class BusListenerTests : IAsyncLifetime
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName Central = MachineName.Parse("central");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly BusIdentity Me = new(Acme, HomeLaptop);

    private static readonly BusRemote Remote = new("acme-org", "acme-bus", "main", new Uri("https://example.invalid/acme-org/acme-bus.git"), null);

    private static readonly DateTimeOffset T = new(2026, 9, 30, 10, 0, 0, TimeSpan.Zero);

    private readonly TempCheckout _checkout = new(Acme, HomeLaptop);

    private readonly RecordingProcessRunner _processes = new();

    private readonly IBusProvider _provider = Substitute.For<IBusProvider>();

    private readonly RecordingLogger<BusListener> _log = new();

    private readonly RecordingLogger<BusRepository> _repositoryLog = new();

    private readonly FakeTimeProvider _clock = new(T);

    private readonly List<Model> _handled = [];

    private InMemorySecretStore _store = null!;

    private JobHandler _handler = null!;

    private int _unpushed;

    public async ValueTask InitializeAsync()
    {
        _store = await TestKeys.StoreWithBothAsync(CancellationToken.None);
        _provider.Remote.Returns(Remote);
        _provider.PullAsync(Arg.Any<CancellationToken>()).Returns(new BusSyncResult(true, string.Empty));
        _provider.PushAsync(Arg.Any<CancellationToken>()).Returns(new PushSucceeded());
        _provider.ProbeHeadAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new Unchanged(4999));
        _handler = (job, _) =>
        {
            _handled.Add(job);
            return Task.FromResult(new JobReply(ReportStatus.Done, null, Encoding.UTF8.GetBytes("ok\n"), T, T.AddSeconds(1)));
        };
        _processes.Hook = spec => RecordingProcessRunner.SubCommand(spec) == "rev-list"
            ? RecordingProcessRunner.Ok(string.Concat(Enumerable.Repeat("commit x\ndeferred\n", _unpushed)))
            : null;
    }

    public ValueTask DisposeAsync()
    {
        _checkout.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task StartAsync_WrongBranch_ReturnsWrongBranchWithoutPull()
    {
        // Arrange
        _processes.On("symbolic-ref", RecordingProcessRunner.Ok("dev\n"));

        // Act
        BusStartupResult result = await Listener().StartAsync(TestContext.Current.CancellationToken);

        // Assert
        result.Should().Be(BusStartupResult.WrongBranch);
        await _provider.DidNotReceive().PullAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_KeyMissing_ReturnsKeyMissing()
    {
        // Act
        BusStartupResult result = await Listener(signWith: new KeyId(Acme, 9)).StartAsync(TestContext.Current.CancellationToken);

        // Assert
        result.Should().Be(BusStartupResult.KeyMissing);
        await _provider.DidNotReceive().PullAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_JobAlreadyWaiting_ProcessedBeforeFirstProbe()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropJobAsync(ct);

        // Act
        BusStartupResult result = await Listener().StartAsync(ct);

        // Assert
        result.Should().Be(BusStartupResult.Clean);
        _handled.Should().ContainSingle();
        await _provider.DidNotReceive().ProbeHeadAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_SecondListener_ListenerRunning()
    {
        // Arrange: patch 0.8.1 — one listener per checkout
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Path.Combine(_checkout.Root, ".git"));
        BusListener first = Listener();
        (await first.StartAsync(ct)).Should().Be(BusStartupResult.Clean);

        // Act
        BusStartupResult second = await Listener().StartAsync(ct);

        // Assert
        second.Should().Be(BusStartupResult.ListenerRunning);
        first.Dispose();
        (await Listener().StartAsync(ct)).Should().Be(BusStartupResult.Clean, "the lock is released when the first listener is disposed");
    }

    [Fact]
    public async Task StartAsync_Clean_StateIsInitialDueNow()
    {
        // Act
        BusListener listener = Listener();
        await listener.StartAsync(TestContext.Current.CancellationToken);

        // Assert
        listener.State.Should().Be(PollState.Initial(T));
    }

    [Fact]
    public async Task ProcessOnceAsync_AcceptedJob_ClaimsThenHandlerThenReport()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model job = await DropJobAsync(ct);
        var order = new List<string>();
        _processes.Hook = spec =>
        {
            order.Add(RecordingProcessRunner.SubCommand(spec));
            return RecordingProcessRunner.SubCommand(spec) == "rev-list" ? RecordingProcessRunner.Ok() : null;
        };
        _handler = (envelope, _) =>
        {
            order.Add("handler");
            _handled.Add(envelope);
            return Task.FromResult(new JobReply(ReportStatus.Done, null, Encoding.UTF8.GetBytes("ok\n"), T, T.AddSeconds(2)));
        };

        // Act
        int handled = await Listener().ProcessOnceAsync(ct);

        // Assert
        handled.Should().Be(1);
        order.Where(o => o != "rev-list").Should().Equal("mv", "commit", "handler", "add", "rm", "commit");
        _handled.Single().Header.Id.Should().Be(job.Header.Id);
        _log.AtLevel(LogLevel.Information).Select(e => e.Message).Should().Equal(
            $"claimed job {job.Header.Id} from central (pushed)",
            $"reported job {job.Header.Id}: done (pushed)");
        string reportPath = _checkout.FilesUnder(BusPaths.Reports(Acme, HomeLaptop)).Single();
        Model report = (await new EnvelopeSigner(_store).ParseAndVerifyAsync(_checkout.Read(reportPath), Acme, ct)).Envelope!;
        report.Report!.DurationSeconds.Should().Be(2);
    }

    [Fact]
    public async Task ProcessOnceAsync_RejectedJob_HandlerNeverCalled()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var id = EnvelopeId.New(T);
        _checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Jobs(Acme, HomeLaptop), id), await TestEnvelopes.SignedJobBytesAsync(Central, Central, _store, TestKeys.Acme1, "probe\n", ct, id: id));

        // Act
        int handled = await Listener().ProcessOnceAsync(ct);

        // Assert
        handled.Should().Be(1);
        _handled.Should().BeEmpty();
        _checkout.FilesUnder(BusPaths.Reports(Acme, HomeLaptop)).Should().ContainSingle();
        _repositoryLog.AtLevel(LogLevel.Warning).Should().ContainSingle();
    }

    [Fact]
    public async Task ProcessOnceAsync_TwoJobs_UlidOrderEachOwnCommits()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model newer = await DropJobAsync(ct, EnvelopeId.New(T.AddMinutes(1)));
        Model older = await DropJobAsync(ct, EnvelopeId.New(T));

        // Act
        int handled = await Listener().ProcessOnceAsync(ct);

        // Assert
        handled.Should().Be(2);
        _handled.Select(j => j.Header.Id).Should().Equal(older.Header.Id, newer.Header.Id);
        _processes.CallsOf("commit").Should().HaveCount(4);
    }

    [Fact]
    public async Task ProcessOnceAsync_ClaimDeferred_HandlerStillCalled()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropJobAsync(ct);
        _provider.PushAsync(Arg.Any<CancellationToken>()).Returns(new Rejected("fetch first"));

        // Act
        Task<int> task = Listener().ProcessOnceAsync(ct);
        while (!task.IsCompleted)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(1, ct);
        }

        // Assert
        (await task).Should().Be(1);
        _handled.Should().ContainSingle();
        _log.AtLevel(LogLevel.Information).Select(e => e.Message).Should().Contain(m => m.EndsWith("(deferred)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProcessOnceAsync_ClaimWriteFailed_HandlerNotCalledErrorLogged()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropJobAsync(ct);
        _processes.On("mv", RecordingProcessRunner.Fail(128, "fatal: bad source"));

        // Act
        int handled = await Listener().ProcessOnceAsync(ct);

        // Assert
        handled.Should().Be(0);
        _handled.Should().BeEmpty();
        _log.AtLevel(LogLevel.Error).Should().ContainSingle().Which.Message.Should().Contain("claim").And.Contain("mv");
    }

    [Fact]
    public async Task ProcessOnceAsync_DeferredCommitPushedByLaterWrite_LogsPushedDeferred()
    {
        // Arrange: one deferred commit sits on HEAD; this write pushes it along
        var ct = TestContext.Current.CancellationToken;
        await DropJobAsync(ct);
        _unpushed = 1;
        _provider.When(p => p.PushAsync(Arg.Any<CancellationToken>())).Do(_ => _unpushed = 0);

        // Act
        await Listener().ProcessOnceAsync(ct);

        // Assert
        _log.AtLevel(LogLevel.Information).Select(e => e.Message).Should().Contain("pushed 1 deferred commit(s)");
    }

    [Fact]
    public async Task ProcessOnceAsync_HandlerThrows_ErrorLoggedJobStaysClaimed()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model job = await DropJobAsync(ct);
        _handler = (_, _) => throw new InvalidOperationException("boom");

        // Act
        int handled = await Listener().ProcessOnceAsync(ct);

        // Assert
        handled.Should().Be(1);
        _log.AtLevel(LogLevel.Error).Should().ContainSingle().Which.Message.Should().Contain("InvalidOperationException").And.Contain(job.Header.Id.Value);
        _processes.CallsOf("commit").Should().ContainSingle();
        _checkout.FilesUnder(BusPaths.Reports(Acme, HomeLaptop)).Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_UnchangedTick_NoLogNoPull()
    {
        // Act
        await RunTicksAsync(Listener(), 3);

        // Assert
        await _provider.Received(3).ProbeHeadAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _provider.Received(1).PullAsync(Arg.Any<CancellationToken>()); // the start-up pull only
        _log.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_ChangedTick_PullsAndProcesses()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        _provider.ProbeHeadAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new Changed("W/\"1\"", 4999), new Unchanged(4998));

        // Act: the job arrives after start-up, so only the Changed tick can pick it up
        BusListener listener = Listener();
        await RunTicksAsync(listener, 2, () => DropJobAsync(ct));

        // Assert
        _handled.Should().ContainSingle();
        listener.State.ETag.Should().Be("W/\"1\"");
        await _provider.Received(1).ProbeHeadAsync("W/\"1\"", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_PhaseChangeToNetworkBackoff_LoggedOnceAcrossRepeatedFailures()
    {
        // Arrange
        _provider.ProbeHeadAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new Unreachable("HttpRequestException"));

        // Act
        await RunTicksAsync(Listener(), 4);

        // Assert
        _log.AtLevel(LogLevel.Warning).Should().ContainSingle().Which.Message.Should().Contain("NetworkBackoff");
    }

    [Fact]
    public async Task RunAsync_RecoveryToPolling_LoggedOnce()
    {
        // Arrange
        _provider.ProbeHeadAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new Unreachable("x"), new Unreachable("x"), new Unchanged(4999), new Unchanged(4999));

        // Act
        await RunTicksAsync(Listener(), 4);

        // Assert
        _log.AtLevel(LogLevel.Warning).Should().ContainSingle();
        _log.AtLevel(LogLevel.Information).Should().ContainSingle().Which.Message.Should().Be("polling");
    }

    [Fact]
    public async Task RunAsync_WaitsUntilNextDue()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        BusListener listener = Listener();
        await listener.StartAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Act: the first probe is due immediately; the second only after the interval
        Task run = listener.RunAsync(PollMode.Active, cts.Token);
        await WaitForProbesAsync(1, ct);
        _clock.Advance(TimeSpan.FromSeconds(7));
        await Task.Delay(50, ct);
        int probesAfterSevenSeconds = _provider.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IBusProvider.ProbeHeadAsync));
        _clock.Advance(TimeSpan.FromSeconds(6));
        await WaitForProbesAsync(2, ct);
        await cts.CancelAsync();
        await run;

        // Assert
        probesAfterSevenSeconds.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_OneInFlightRequest()
    {
        // Arrange: the first probe hangs until released
        var ct = TestContext.Current.CancellationToken;
        var gate = new TaskCompletionSource<HeadProbe>(TaskCreationOptions.RunContinuationsAsynchronously);
        _provider.ProbeHeadAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(_ => gate.Task, _ => Task.FromResult<HeadProbe>(new Unchanged(4999)));
        BusListener listener = Listener();
        await listener.StartAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Act
        Task run = listener.RunAsync(PollMode.Active, cts.Token);
        await WaitForProbesAsync(1, ct);
        _clock.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(50, ct);
        int probesWhileHanging = _provider.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IBusProvider.ProbeHeadAsync));
        gate.SetResult(new Unchanged(4999));
        await Task.Delay(50, ct);
        _clock.Advance(TimeSpan.FromSeconds(15));
        await WaitForProbesAsync(2, ct, run);
        await cts.CancelAsync();
        await run;

        // Assert
        probesWhileHanging.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_Cancelled_EndsWithoutException()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        BusListener listener = Listener();
        await listener.StartAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Act
        Task run = listener.RunAsync(PollMode.Active, cts.Token);
        await WaitForProbesAsync(1, ct);
        await cts.CancelAsync();
        Func<Task> act = () => run;

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Logs_EveryEntryHasTenantMachinePhaseScope()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        _provider.ProbeHeadAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new Unreachable("x"), new Changed("W/\"1\"", 4999), new Unchanged(4999));

        // Act
        await RunTicksAsync(Listener(), 3, () => DropJobAsync(ct));

        // Assert
        _log.Entries.Should().NotBeEmpty();
        _log.Entries.Should().AllSatisfy(e =>
        {
            var scope = e.Scopes.OfType<Dictionary<string, object>>().Should().ContainSingle().Subject;
            scope.Should().ContainKey("tenant").WhoseValue.Should().Be("acme");
            scope.Should().ContainKey("machine").WhoseValue.Should().Be("home-laptop");
            scope.Should().ContainKey("phase");
        });
        _log.Entries.Select(e => (string)((Dictionary<string, object>)e.Scopes.Single()!)["phase"]).Should().Equal("poll", "poll", "claim", "report");
    }

    [Fact]
    public async Task SecretSweep_Clean()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        _provider.ProbeHeadAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new Changed("W/\"1\"", 4999), new Unchanged(4999));

        // Act
        await RunTicksAsync(Listener(), 2, () => DropJobAsync(ct));

        // Assert
        SecretSweep.AssertAbsent(_processes.Calls.SelectMany(SecretSweep.TextsOf));
        SecretSweep.AssertAbsent(_log.Messages);
        SecretSweep.AssertAbsent(_repositoryLog.Messages);
    }

    private BusListener Listener(KeyId? signWith = null)
    {
        var git = new GitClient(_processes, new GitClientOptions { Identity = new GitIdentity("zyggy (home-laptop)", "home-laptop@zyggy.org") }, _clock);
        var repository = new BusRepository(git, _provider, Me, _checkout.Root, new EnvelopeSigner(_store), _clock, () => 0, _repositoryLog);
        return new BusListener(_provider, repository, PollOptions.Default, signWith ?? TestKeys.Acme1, _clock, new Random(1), (job, ct) => _handler(job, ct), _log);
    }

    /// <summary>Runs the loop for <paramref name="ticks"/> probes, advancing the fake clock to each next-due instant.</summary>
    private async Task RunTicksAsync(BusListener listener, int ticks, Func<Task>? afterStart = null)
    {
        var ct = TestContext.Current.CancellationToken;
        await listener.StartAsync(ct);
        if (afterStart is not null)
        {
            await afterStart();
        }

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

    private async Task<Model> DropJobAsync(CancellationToken ct, EnvelopeId? id = null)
    {
        Model job = await TestEnvelopes.SignedJobAsync(Central, HomeLaptop, _store, TestKeys.Acme1, "probe\n", ct, id);
        _checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Jobs(Acme, HomeLaptop), job.Header.Id), EnvelopeWriter.Serialize(job));
        return job;
    }
}
