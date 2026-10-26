using System.Text;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Git;
using Zyggy.Core.Jobs;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

using AdmissionDeferred = Zyggy.Core.Jobs.Deferred;
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
    public async Task StartAsync_StoreUnavailable_ReturnsStoreUnavailableWithoutLockOrPull()
    {
        // Act: spec 09 AC-2 — a store that cannot be opened is its own outcome, before the listen lock and the pull
        BusStartupResult result = await Listener(store: new ThrowingSecretStore()).StartAsync(TestContext.Current.CancellationToken);

        // Assert
        result.Should().Be(BusStartupResult.StoreUnavailable);
        await _provider.DidNotReceive().PullAsync(Arg.Any<CancellationToken>());
        File.Exists(Path.Join(_checkout.Root, ".git", BusCheckoutLock.ListenLockName)).Should().BeFalse();
    }

    [Fact]
    public async Task StartAsync_KeyMissing_StartupDetailNamesStoreAndCommand()
    {
        // Arrange
        BusListener listener = Listener(signWith: new KeyId(Acme, 9));

        // Act
        await listener.StartAsync(TestContext.Current.CancellationToken);

        // Assert
        listener.StartupDetail.Should().Be("signing key acme/9 is not in the memory; run: zyggy secret set hmac/9 --hex");
    }

    [Fact]
    public async Task StartAsync_StoreUnavailable_StartupDetailStartsWithSecretStoreUnavailable()
    {
        // Arrange
        BusListener listener = Listener(store: new ThrowingSecretStore());

        // Act
        await listener.StartAsync(TestContext.Current.CancellationToken);

        // Assert
        listener.StartupDetail.Should().Be("secret store unavailable: credential-manager: 1312 No such logon session");
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
    public async Task ProcessOnceAsync_HandlerReplyWithMetrics_ReportCarriesThem()
    {
        // Arrange: spec 06 ex-Q8 — the job runner's metrics reach the §4 report fields
        var ct = TestContext.Current.CancellationToken;
        await DropJobAsync(ct);
        _handler = (_, _) => Task.FromResult(new JobReply(ReportStatus.Done, null, Encoding.UTF8.GetBytes("REPORT\nok\n"), T, T.AddSeconds(65))
        {
            CostUsd = 0.42m,
            NumTurns = 12,
            InputTokens = 1200,
            OutputTokens = 300,
            Model = "claude-sonnet-4-5",
            FilesChanged = [],
            DiffRef = null,
        });

        // Act
        await Listener().ProcessOnceAsync(ct);

        // Assert
        string reportPath = _checkout.FilesUnder(BusPaths.Reports(Acme, HomeLaptop)).Single();
        Model report = (await new EnvelopeSigner(_store).ParseAndVerifyAsync(_checkout.Read(reportPath), Acme, ct)).Envelope!;
        report.Report!.CostUsd.Should().Be(0.42m);
        report.Report.NumTurns.Should().Be(12);
        report.Report.InputTokens.Should().Be(1200);
        report.Report.OutputTokens.Should().Be(300);
        report.Report.Model.Should().Be("claude-sonnet-4-5");
        report.Report.FilesChanged.Should().NotBeNull().And.BeEmpty();
        report.Report.DiffRef.Should().BeNull();
        report.Report.DurationSeconds.Should().Be(65);
    }

    [Fact]
    public async Task ProcessOnceAsync_ProbeReply_ReportHasNoMetricFields()
    {
        // Arrange: a reply without metrics (the probe's) writes the same report as before 06
        var ct = TestContext.Current.CancellationToken;
        await DropJobAsync(ct);

        // Act
        await Listener().ProcessOnceAsync(ct);

        // Assert
        string reportPath = _checkout.FilesUnder(BusPaths.Reports(Acme, HomeLaptop)).Single();
        Model report = (await new EnvelopeSigner(_store).ParseAndVerifyAsync(_checkout.Read(reportPath), Acme, ct)).Envelope!;
        report.FrontMatter.Entries.Keys.Should().NotContain(["cost_usd", "num_turns", "input_tokens", "output_tokens", "model", "files_changed", "diff_ref"]);
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

    // ----- plan 07 Step 4: the gate before the claim, tracked runs, the status snapshot (AC-8, AC-9) -----

    [Fact]
    public async Task ProcessOnceAsync_GateDefers_NoClaimNoHandlerNotHandled()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model job = await DropJobAsync(ct);
        BusListener listener = Listener(gate: Gate(() => new AdmissionDeferred(DeferralReason.OutsideWorkHours)));

        // Act
        int handled = await listener.ProcessOnceAsync(ct);

        // Assert: §6 step 3 — the job is left exactly where it is
        handled.Should().Be(0);
        _processes.CallsOf("mv").Should().BeEmpty();
        _checkout.Exists(BusPaths.EnvelopeFile(BusPaths.Jobs(Acme, HomeLaptop), job.Header.Id)).Should().BeTrue();
        _handled.Should().BeEmpty();
        listener.Status.JobsRunning.Should().Be(0);
        _log.Entries.Should().BeEmpty("the dispatcher logs a deferral, the listener does not");
    }

    [Fact]
    public async Task ProcessOnceAsync_GateAdmits_ClaimedAndRunTracked()
    {
        // Arrange: the handler blocks until released
        var ct = TestContext.Current.CancellationToken;
        Model job = await DropJobAsync(ct);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler = async (envelope, _) =>
        {
            await release.Task;
            _handled.Add(envelope);
            return new JobReply(ReportStatus.Done, null, Encoding.UTF8.GetBytes("ok\n"), T, T.AddSeconds(1));
        };
        var lease = new TrackingLease();
        BusListener listener = Listener(gate: Gate(() => new Admitted(lease)));

        // Act
        int handled = await listener.ProcessOnceAsync(ct);
        int runningWhilePending = listener.Status.JobsRunning;
        IReadOnlyList<string> reportsWhilePending = _checkout.FilesUnder(BusPaths.Reports(Acme, HomeLaptop));
        release.SetResult();
        await listener.DrainAsync(ct);

        // Assert
        handled.Should().Be(1);
        _processes.CallsOf("mv").Should().ContainSingle();
        runningWhilePending.Should().Be(1);
        reportsWhilePending.Should().BeEmpty();
        _checkout.FilesUnder(BusPaths.Reports(Acme, HomeLaptop)).Should().ContainSingle();
        _handled.Single().Header.Id.Should().Be(job.Header.Id);
        listener.Status.JobsRunning.Should().Be(0);
        listener.Status.LastReportUtc.Should().Be(T);
        lease.Disposals.Should().Be(1);
    }

    [Fact]
    public async Task ProcessOnceAsync_RejectedFile_BypassesGate()
    {
        // Arrange: a job addressed to central lands in the home laptop's inbox
        var ct = TestContext.Current.CancellationToken;
        Model wrong = await TestEnvelopes.SignedJobAsync(Central, Central, _store, TestKeys.Acme1, "probe\n", ct);
        _checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Jobs(Acme, HomeLaptop), wrong.Header.Id), EnvelopeWriter.Serialize(wrong));
        var gateCalls = 0;
        BusListener listener = Listener(gate: Gate(() =>
        {
            gateCalls++;
            return new Admitted(new TrackingLease());
        }));

        // Act
        int handled = await listener.ProcessOnceAsync(ct);

        // Assert
        handled.Should().Be(1, "the rejection is handled");
        gateCalls.Should().Be(0);
        _handled.Should().BeEmpty();
        listener.Status.JobsRunning.Should().Be(0);
    }

    [Fact]
    public async Task ProcessOnceAsync_ClaimFails_LeaseDisposed()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropJobAsync(ct);
        _processes.On("mv", RecordingProcessRunner.Fail(1, "fatal: mv failed"));
        var lease = new TrackingLease();
        BusListener listener = Listener(gate: Gate(() => new Admitted(lease)));

        // Act
        int handled = await listener.ProcessOnceAsync(ct);

        // Assert
        handled.Should().Be(0);
        lease.Disposals.Should().Be(1);
        listener.Status.JobsRunning.Should().Be(0);
        _handled.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_TwoAdmittedBlockingJobs_LoopKeepsProbing()
    {
        // Arrange: two jobs whose handlers block on their own gate
        var ct = TestContext.Current.CancellationToken;
        Model first = await DropJobAsync(ct, EnvelopeId.New(T.AddSeconds(-2)));
        Model second = await DropJobAsync(ct, EnvelopeId.New(T.AddSeconds(-1)));
        var releases = new Dictionary<string, TaskCompletionSource>
        {
            [first.Header.Id.Value] = new(TaskCreationOptions.RunContinuationsAsynchronously),
            [second.Header.Id.Value] = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        _handler = async (envelope, _) =>
        {
            await releases[envelope.Header.Id.Value].Task;
            return new JobReply(ReportStatus.Done, null, Encoding.UTF8.GetBytes("ok\n"), T, T.AddSeconds(1));
        };
        BusListener listener = Listener(gate: Gate(() => new Admitted(new TrackingLease())));
        await listener.StartAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Act: three probes while both runs are pending
        Task run = listener.RunAsync(PollMode.Active, cts.Token);
        for (int i = 1; i <= 3; i++)
        {
            await WaitForProbesAsync(i, ct, run);
            await Task.Delay(20, ct);
            _clock.SetUtcNow(listener.State.NextDueUtc);
        }

        int runningWhilePending = listener.Status.JobsRunning;
        releases[first.Header.Id.Value].SetResult();
        // the report file lands before the run's lease is released: wait for both, not for the file alone
        for (int i = 0; i < 300 && (_checkout.FilesUnder(BusPaths.Reports(Acme, HomeLaptop)).Count == 0 || listener.Status.JobsRunning != 1); i++)
        {
            await Task.Delay(10, ct);
        }

        int reportsAfterFirst = _checkout.FilesUnder(BusPaths.Reports(Acme, HomeLaptop)).Count;
        int runningAfterFirst = listener.Status.JobsRunning;
        releases[second.Header.Id.Value].SetResult();
        await listener.DrainAsync(ct);
        await cts.CancelAsync();
        await run;

        // Assert
        _provider.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IBusProvider.ProbeHeadAsync)).Should().BeGreaterThanOrEqualTo(3);
        runningWhilePending.Should().Be(2);
        reportsAfterFirst.Should().Be(1);
        runningAfterFirst.Should().Be(1);
        _checkout.FilesUnder(BusPaths.Reports(Acme, HomeLaptop)).Should().HaveCount(2);
        listener.Status.JobsRunning.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_CancelledDuringRun_HandlerSeesCancellationNoReportDrains()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropJobAsync(ct);
        _handler = async (_, handlerCt) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, handlerCt);
            throw new InvalidOperationException("unreachable");
        };
        BusListener listener = Listener(gate: Gate(() => new Admitted(new TrackingLease())));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await listener.StartAsync(cts.Token);
        listener.Status.JobsRunning.Should().Be(1);

        // Act
        await cts.CancelAsync();
        await listener.DrainAsync(ct);

        // Assert: no report, the claim stays
        _checkout.FilesUnder(BusPaths.Reports(Acme, HomeLaptop)).Should().BeEmpty();
        _processes.CallsOf("rm").Should().BeEmpty();
        listener.Status.JobsRunning.Should().Be(0);
        _log.AtLevel(LogLevel.Error).Should().BeEmpty("cancellation is a normal stop, not a handler failure");
    }

    [Fact]
    public async Task Status_AfterStartAndTick_CarriesPollPullHeadShaAndClaimedCount()
    {
        // Arrange: one file of a previous run sits in claimed/
        var ct = TestContext.Current.CancellationToken;
        _checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Claimed(Acme, HomeLaptop), EnvelopeId.New(T.AddMinutes(-9))), [1]);
        BusListener listener = Listener();

        // Act
        await listener.StartAsync(ct);
        ListenerStatus afterStart = listener.Status;
        await RunTicksAsync(listener, 1);

        // Assert
        afterStart.LastPullUtc.Should().Be(T);
        afterStart.BusHeadSha.Should().Be("0123456789abcdef0123456789abcdef01234567");
        afterStart.JobsClaimedByMe.Should().Be(1);
        afterStart.LastPollUtc.Should().BeNull();
        listener.Status.LastPollUtc.Should().Be(T);
        listener.Status.Poll.Should().Be(listener.State);
        listener.Status.Mode.Should().Be(PollMode.Active);
    }

    [Fact]
    public async Task ProcessOnceAsync_NoGate_AdmitsAllAsBefore()
    {
        // Arrange: 04's probe path — no gate, the run awaited inline
        var ct = TestContext.Current.CancellationToken;
        await DropJobAsync(ct);

        // Act
        int handled = await Listener().ProcessOnceAsync(ct);

        // Assert
        handled.Should().Be(1);
        _handled.Should().ContainSingle();
        _checkout.FilesUnder(BusPaths.Reports(Acme, HomeLaptop)).Should().ContainSingle("the report is written before ProcessOnceAsync returns");
    }

    // ----- plan 07 Step 5: the safety fetch and the mode selector (AC-6 second half, AC-7) -----

    [Fact]
    public async Task RunAsync_UnchangedForever_SafetyFetchAt15And30MinutesNotBefore()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        BusListener listener = Listener();
        await listener.StartAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Act
        Task run = listener.RunAsync(PollMode.Active, cts.Token);
        await WaitForProbesAsync(1, ct, run);
        _clock.SetUtcNow(T.AddMinutes(14).AddSeconds(59));
        await Task.Delay(100, ct);
        int fetchesBefore = _processes.CallsOf("fetch").Count();
        _clock.SetUtcNow(T.AddMinutes(15));
        await WaitUntilAsync(() => _processes.CallsOf("fetch").Any(), ct, run);
        int fetchesAtFifteen = _processes.CallsOf("fetch").Count();
        _clock.SetUtcNow(T.AddMinutes(30));
        await WaitUntilAsync(() => _processes.CallsOf("fetch").Count() >= 2, ct, run);
        await cts.CancelAsync();
        await run;

        // Assert
        fetchesBefore.Should().Be(0);
        fetchesAtFifteen.Should().Be(1);
        _processes.CallsOf("fetch").Should().HaveCount(2);
        _log.AtLevel(LogLevel.Warning).Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_SafetyFetchMoved_WarnsPullsProcessesAndResetsETag()
    {
        // Arrange: the doorbell says Changed once (an ETag is stored), then Unchanged forever while the branch moves behind it
        var ct = TestContext.Current.CancellationToken;
        _provider.ProbeHeadAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new Changed("etag-1", 4999), new Unchanged(4999));
        BusListener listener = Listener();
        await listener.StartAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Act
        Task run = listener.RunAsync(PollMode.Active, cts.Token);
        await WaitForProbesAsync(1, ct, run);
        await Task.Delay(50, ct);
        listener.State.ETag.Should().Be("etag-1");
        int pullsBefore = PullCalls();
        await DropJobAsync(ct);
        _processes.Hook = spec => spec.Arguments.Contains("origin/main") ? RecordingProcessRunner.Ok("abcdef0123456789abcdef0123456789abcdef01\n") : null;
        _clock.SetUtcNow(T.AddMinutes(15));
        await WaitUntilAsync(() => _handled.Count == 1, ct, run);
        // the report's write sequence (its pull included) finishes after the handler returned: wait for the pull count itself, not for the handler
        await WaitUntilAsync(() => PullCalls() >= pullsBefore + 1 + WritePulls, ct, run);
        await listener.DrainAsync(ct);
        await cts.CancelAsync();
        await run;

        // Assert
        _log.AtLevel(LogLevel.Warning).Select(e => e.Message).Should().Equal("safety fetch: branch moved while the doorbell said unchanged; resynchronising");
        _handled.Should().ContainSingle();
        PullCalls().Should().Be(pullsBefore + 1 + WritePulls, "the safety fetch pulls once; the claim and the report each pull inside the §5 write sequence");
        listener.State.ETag.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_SafetyFetchUnchanged_NoLogLine()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        BusListener listener = Listener();
        await listener.StartAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Act
        Task run = listener.RunAsync(PollMode.Active, cts.Token);
        await WaitForProbesAsync(1, ct, run);
        _clock.SetUtcNow(T.AddMinutes(15));
        await WaitUntilAsync(() => _processes.CallsOf("fetch").Any(), ct, run);
        await Task.Delay(50, ct);
        await cts.CancelAsync();
        await run;

        // Assert: §11 — a quiet safety fetch is silent
        _log.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_SafetyFetchFailsTwice_OneWarning()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        _processes.On("fetch", RecordingProcessRunner.Fail(128, "fatal: unable to access"), RecordingProcessRunner.Fail(128, "fatal: unable to access"));
        BusListener listener = Listener();
        await listener.StartAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Act
        Task run = listener.RunAsync(PollMode.Active, cts.Token);
        await WaitForProbesAsync(1, ct, run);
        _clock.SetUtcNow(T.AddMinutes(15));
        await WaitUntilAsync(() => _processes.CallsOf("fetch").Any(), ct, run);
        _clock.SetUtcNow(T.AddMinutes(30));
        await WaitUntilAsync(() => _processes.CallsOf("fetch").Count() >= 2, ct, run);
        await Task.Delay(50, ct);
        await cts.CancelAsync();
        await run;

        // Assert: once per streak
        _log.AtLevel(LogLevel.Warning).Select(e => e.Message).Should().Equal("safety fetch failed: fatal: unable to access");
    }

    [Fact]
    public async Task RunAsync_SafetyFetchAndChangedSameTick_OnePullOnePass()
    {
        // Arrange: at the 15-minute tick both the safety fetch (Moved) and the doorbell (Changed) say the branch moved
        var ct = TestContext.Current.CancellationToken;
        _provider.ProbeHeadAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new Unchanged(4999), new Changed("etag-2", 4999), new Unchanged(4999));
        BusListener listener = Listener();
        await listener.StartAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Act
        Task run = listener.RunAsync(PollMode.Active, cts.Token);
        await WaitForProbesAsync(1, ct, run);
        int pullsBefore = PullCalls();
        await DropJobAsync(ct);
        _processes.Hook = spec => spec.Arguments.Contains("origin/main") ? RecordingProcessRunner.Ok("abcdef0123456789abcdef0123456789abcdef01\n") : null;
        _clock.SetUtcNow(T.AddMinutes(15));
        await WaitForProbesAsync(2, ct, run);
        await WaitUntilAsync(() => _handled.Count >= 1, ct, run);
        await listener.DrainAsync(ct);
        await cts.CancelAsync();
        await run;

        // Assert
        _handled.Should().ContainSingle();
        PullCalls().Should().Be(pullsBefore + 1 + WritePulls, "one pull serves both signals; the claim and the report each pull inside the §5 write sequence");
    }

    [Fact]
    public async Task RunAsync_ModeSelectorIdleThenActive_LogsOnceAndProcessesWithoutPull()
    {
        // Arrange: a job waits in jobs/, deferred by the gate until the window opens with the mode
        var ct = TestContext.Current.CancellationToken;
        await DropJobAsync(ct);
        var inside = false;
        BusListener listener = Listener(gate: Gate(() => inside ? new Admitted(new TrackingLease()) : new AdmissionDeferred(DeferralReason.OutsideWorkHours)));
        PollModeSelector selector = _ => inside ? PollMode.Active : PollMode.Idle;
        await listener.StartAsync(ct);
        int pullsAfterStart = PullCalls();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Act: two idle ticks, then the window opens
        Task run = listener.RunAsync(selector, cts.Token);
        await WaitForProbesAsync(1, ct, run);
        await Task.Delay(20, ct);
        _clock.SetUtcNow(listener.State.NextDueUtc);
        await WaitForProbesAsync(2, ct, run);
        await Task.Delay(20, ct);
        inside = true;
        _clock.SetUtcNow(listener.State.NextDueUtc);
        await WaitForProbesAsync(3, ct, run);
        await WaitUntilAsync(() => _handled.Count == 1, ct, run);
        await listener.DrainAsync(ct);
        await cts.CancelAsync();
        await run;

        // Assert
        _log.AtLevel(LogLevel.Information).Select(e => e.Message).Where(m => m.StartsWith("poll mode", StringComparison.Ordinal))
            .Should().Equal("poll mode Idle", "poll mode Active");
        PullCalls().Should().Be(pullsAfterStart + WritePulls, "an Idle → Active transition processes the inbox without a pull of its own; the claim and the report each pull inside the §5 write sequence");
        listener.Status.Mode.Should().Be(PollMode.Active);
    }

    [Fact]
    public async Task RunAsync_IdleMode_NextDueUsesIdleInterval()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        BusListener listener = Listener();
        await listener.StartAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Act
        Task run = listener.RunAsync(_ => PollMode.Idle, cts.Token);
        await WaitForProbesAsync(1, ct, run);
        await Task.Delay(20, ct);
        TimeSpan untilNext = listener.State.NextDueUtc - T;
        await cts.CancelAsync();
        await run;

        // Assert
        untilNext.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(58)).And.BeLessThanOrEqualTo(TimeSpan.FromSeconds(62));
        listener.Status.Mode.Should().Be(PollMode.Idle);
    }

    [Fact]
    public async Task RunAsync_PollModeOverload_StillActiveForever()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        BusListener listener = Listener();

        // Act
        await RunTicksAsync(listener, 2);

        // Assert: 04's overload — Active, 10 s ± 2 s, no mode line
        (listener.State.NextDueUtc - _clock.GetUtcNow()).Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(12));
        listener.Status.Mode.Should().Be(PollMode.Active);
        _log.Messages.Should().NotContain(m => m.StartsWith("poll mode", StringComparison.Ordinal));
    }

    /// <summary>The pulls one handled job costs on its own: the claim's and the report's write sequences each start with one (§5).</summary>
    private const int WritePulls = 2;

    private int PullCalls() => _provider.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IBusProvider.PullAsync));

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct, Task? run = null)
    {
        for (int i = 0; i < 500; i++)
        {
            if (run is { IsFaulted: true })
            {
                await run;
            }

            if (condition())
            {
                return;
            }

            await Task.Delay(10, ct);
        }

        throw new TimeoutException("the condition did not hold in time");
    }

    // ----- plan 07 Step 6: resuming the node's own claims (AC-10) -----

    [Fact]
    public async Task ResumeClaimedAsync_OneVerifyingOneTampered_RunsOneWarnsOnceLogsResumingOnce()
    {
        // Arrange: two files of a previous run in claimed/, one of them tampered
        var ct = TestContext.Current.CancellationToken;
        var goodId = EnvelopeId.New(T.AddSeconds(-2));
        var badId = EnvelopeId.New(T.AddSeconds(-1));
        _checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Claimed(Acme, HomeLaptop), goodId), await TestEnvelopes.SignedJobBytesAsync(Central, HomeLaptop, _store, TestKeys.Acme1, "probe\n", ct, id: goodId));
        _checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Claimed(Acme, HomeLaptop), badId), await TestEnvelopes.SignedJobBytesAsync(Central, HomeLaptop, _store, TestKeys.Acme1, "probe\n", ct, b => { b[^2] ^= 0x01; return b; }, badId));
        BusListener listener = Listener();

        // Act
        int resumed = await listener.ResumeClaimedAsync(ct);

        // Assert
        resumed.Should().Be(1);
        _processes.CallsOf("mv").Should().BeEmpty("a resumed job is already claimed");
        _handled.Select(j => j.Header.Id).Should().Equal(goodId);
        _checkout.FilesUnder(BusPaths.Reports(Acme, HomeLaptop)).Should().ContainSingle();
        _checkout.Exists(BusPaths.EnvelopeFile(BusPaths.Claimed(Acme, HomeLaptop), badId)).Should().BeTrue("the sweep decides about a claim that does not verify");
        _log.AtLevel(LogLevel.Warning).Select(e => e.Message).Should().Equal($"claimed job {badId.Value} does not verify; left for the sweep");
        _log.Messages.Where(m => m == "resuming 2 claimed job(s) from a previous run").Should().ContainSingle();
    }

    [Fact]
    public async Task ResumeClaimedAsync_ClaimHeldByThisListener_NotResumedAndSilent()
    {
        // Arrange: plan 07 Step 11 — a job this listener claimed in its start-up pass and is still running is not a leftover
        var ct = TestContext.Current.CancellationToken;
        await DropJobAsync(ct);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler = async (envelope, _) =>
        {
            await release.Task;
            _handled.Add(envelope);
            return new JobReply(ReportStatus.Done, null, Encoding.UTF8.GetBytes("ok\n"), T, T.AddSeconds(1));
        };
        BusListener listener = Listener(gate: Gate(() => new Admitted(new TrackingLease())));
        await listener.ProcessOnceAsync(ct);
        listener.Status.JobsRunning.Should().Be(1);

        // Act
        int resumed = await listener.ResumeClaimedAsync(ct);
        release.SetResult();
        await listener.DrainAsync(ct);

        // Assert
        resumed.Should().Be(0);
        _handled.Should().ContainSingle("the job runs once");
        _log.Messages.Should().NotContain(m => m.StartsWith("resuming", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResumeClaimedAsync_Empty_ZeroAndSilent()
    {
        // Act
        int resumed = await Listener().ResumeClaimedAsync(TestContext.Current.CancellationToken);

        // Assert
        resumed.Should().Be(0);
        _log.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task ResumeClaimedAsync_GateDefers_LeftInClaimedThenRunOnLaterPass()
    {
        // Arrange: a claimed normal job outside work hours waits inside its claim
        var ct = TestContext.Current.CancellationToken;
        var id = EnvelopeId.New(T);
        string claimedPath = BusPaths.EnvelopeFile(BusPaths.Claimed(Acme, HomeLaptop), id);
        _checkout.Drop(claimedPath, await TestEnvelopes.SignedJobBytesAsync(Central, HomeLaptop, _store, TestKeys.Acme1, "probe\n", ct, id: id));
        var inside = false;
        BusListener listener = Listener(gate: Gate(() => inside ? new Admitted(new TrackingLease()) : new AdmissionDeferred(DeferralReason.OutsideWorkHours)));

        // Act
        int resumed = await listener.ResumeClaimedAsync(ct);
        int handledWhileOutside = _handled.Count;
        inside = true;
        await listener.ProcessOnceAsync(ct);
        await listener.DrainAsync(ct);

        // Assert
        resumed.Should().Be(1);
        handledWhileOutside.Should().Be(0);
        _handled.Select(j => j.Header.Id).Should().Equal(id);
        _processes.CallsOf("mv").Should().BeEmpty();
        _checkout.FilesUnder(BusPaths.Reports(Acme, HomeLaptop)).Should().ContainSingle();
    }

    [Fact]
    public async Task ResumeClaimedAsync_HandlerCancelled_ClaimStays()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var id = EnvelopeId.New(T);
        string claimedPath = BusPaths.EnvelopeFile(BusPaths.Claimed(Acme, HomeLaptop), id);
        _checkout.Drop(claimedPath, await TestEnvelopes.SignedJobBytesAsync(Central, HomeLaptop, _store, TestKeys.Acme1, "probe\n", ct, id: id));
        _handler = async (_, handlerCt) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, handlerCt);
            throw new InvalidOperationException("unreachable");
        };
        BusListener listener = Listener(gate: Gate(() => new Admitted(new TrackingLease())));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Act
        int resumed = await listener.ResumeClaimedAsync(cts.Token);
        int runningWhilePending = listener.Status.JobsRunning;
        await cts.CancelAsync();
        await listener.DrainAsync(ct);

        // Assert
        resumed.Should().Be(1);
        runningWhilePending.Should().Be(1);
        _checkout.FilesUnder(BusPaths.Reports(Acme, HomeLaptop)).Should().BeEmpty();
        _processes.CallsOf("rm").Should().BeEmpty();
        _checkout.Exists(claimedPath).Should().BeTrue();
        listener.Status.JobsRunning.Should().Be(0);
    }

    private BusListener Listener(KeyId? signWith = null, JobGate? gate = null, ISecretStore? store = null)
    {
        var git = new GitClient(_processes, new GitClientOptions { Identity = new GitIdentity("zyggy (home-laptop)", "home-laptop@zyggy.org") }, _clock);
        var repository = new BusRepository(git, _provider, Me, _checkout.Root, new EnvelopeSigner(store ?? _store), _clock, () => 0, _repositoryLog);
        return new BusListener(_provider, repository, PollOptions.Default, signWith ?? TestKeys.Acme1, _clock, new Random(1), (job, ct) => _handler(job, ct), _log, gate);
    }

    private static JobGate Gate(Func<AdmissionDecision> decide) => (_, _) => Task.FromResult(decide());

    /// <summary>A lease that counts its disposals (the listener must release every admitted job exactly once).</summary>
    private sealed class TrackingLease : IDisposable
    {
        public int Disposals { get; private set; }

        public void Dispose() => Disposals++;
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
