using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Health;
using Zyggy.Core.Registry;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Cli;
using Zyggy.Integration.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Bus;

/// <summary>
/// Spec 61 slice 6 (AC-10, AC-11), the slice's review check: the home laptop's node writes the registry key <c>running</c> in its claim's
/// and its report's own commit, republishes it at start-up and at a noted wake-up; <c>zyggy sweep</c> on Central waits for a claim the
/// registry lists and acts as today once it does not, or for a 0.30.0 node that never writes the key. Real git, a bare repository, the
/// production node composition with <c>tools/fake-claude</c>, Central's verb in process.
/// </summary>
public sealed class RunningRegistryTests : IAsyncLifetime
{
    /// <summary>How far ahead Central's clock runs: a claim committed just now is past its 2-minute timeout + 15 min.</summary>
    private static readonly TimeSpan Ahead = TimeSpan.FromHours(1);

    private static readonly DateTimeOffset Seen = new(2026, 11, 24, 20, 0, 0, TimeSpan.Zero);

    private static readonly string RegistryPath = BusPaths.Registry(CliHarness.Acme, CliHarness.HomeLaptop);

    private readonly JumpClock _clock = new();

    private CliHarness _harness = null!;

    private ScratchProject _project = null!;

    private BusRepoFixture Bus => _harness.Bus;

    public async ValueTask InitializeAsync()
    {
        _harness = await CliHarness.CreateAsync(CancellationToken.None);
        _project = await ScratchProject.CreateAsync(Bus, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        _project?.Dispose();
        await _harness.DisposeAsync();
    }

    // AC-10: the claim commit lists the job under running, the report commit no longer does; the rest of the file is the node's own.
    [Fact]
    public async Task Node_ClaimAndReport_RegistryListsTheJobOnlyBetweenThem()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await SeedRegistryAsync("0.30.1", [], ct);
        Model job = await SeedQueuedJobAsync(ct);
        await using ServiceProvider node = BuildNode();

        // Act
        (await node.GetRequiredService<BusListener>().StartAsync(ct)).Should().Be(BusStartupResult.Clean);

        // Assert
        string claim = await CommitAsync("--grep=acme claim " + job.Header.Id.Value, ct);
        string report = await CommitAsync("--grep=acme report ", ct);
        new
        {
            AtClaim = (await RegistryAtAsync(claim, ct)).Running,
            AtReport = (await RegistryAtAsync(report, ct)).Running,
            OnMain = (await RegistryAtAsync("main", ct)).Version,
        }.Should().BeEquivalentTo(new { AtClaim = new[] { job.Header.Id }, AtReport = Array.Empty<EnvelopeId>(), OnMain = "0.30.1" });
    }

    // AC-10: a node that crashed with a run listed republishes at start-up without it.
    [Fact]
    public async Task Node_StartUpAfterACrash_RegistryRepublishedWithoutTheLostRun()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var lost = EnvelopeId.New(DateTimeOffset.UtcNow);
        await SeedRegistryAsync("0.30.1", [lost], ct);
        await using ServiceProvider node = BuildNode();

        // Act
        (await node.GetRequiredService<BusListener>().StartAsync(ct)).Should().Be(BusStartupResult.Clean);

        // Assert
        new { (await RegistryAtAsync("main", ct)).Running, Subject = (await Bus.LastCommitSubjectAsync(ct)).Trim() }
            .Should().BeEquivalentTo(new { Running = Array.Empty<EnvelopeId>(), Subject = "acme registry home-laptop" });
    }

    // AC-10: a clean start with nothing to correct adds no commit.
    [Fact]
    public async Task Node_StartUpNothingRunning_NoRegistryCommit()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await SeedRegistryAsync("0.30.1", [], ct);
        int before = await _harness.CommitCountOnMainAsync(ct);
        await using ServiceProvider node = BuildNode();

        // Act
        (await node.GetRequiredService<BusListener>().StartAsync(ct)).Should().Be(BusStartupResult.Clean);

        // Assert
        (await _harness.CommitCountOnMainAsync(ct)).Should().Be(before);
    }

    // Spec 61 §3 (left to this slice by slice 3): at a noted wake-up the node pulls and refreshes its registry at once.
    [Fact]
    public async Task Node_WakeUp_RegistryRefreshedAtOnce()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await SeedRegistryAsync("0.30.1", [], ct);
        await using ServiceProvider node = BuildNode();
        BusListener listener = node.GetRequiredService<BusListener>();
        (await listener.StartAsync(ct)).Should().Be(BusStartupResult.Clean);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task loop = listener.RunAsync(PollMode.Active, cts.Token);
        await WaitForFirstPollAsync(listener, ct);

        // Act: the machine sleeps 9 hours while the loop waits for its next tick (the wall clock jumps; the timers stay real)
        _clock.Jump(TimeSpan.FromHours(9));
        DateTimeOffset? lastSeen = await WaitForLastSeenAfterAsync(DateTimeOffset.UtcNow.AddHours(8), ct);
        await cts.CancelAsync();
        await loop;

        // Assert
        lastSeen.Should().BeAfter(DateTimeOffset.UtcNow.AddHours(8));
    }

    // AC-11 and the review check: `zyggy sweep --dry-run` names the waiting claim and re-queues nothing.
    [Fact]
    public async Task SweepDryRun_ClaimListedAsRunning_PrintsWaitingLine()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model job = await SeedClaimAsync(ct);
        await SeedRegistryAsync("0.30.1", [job.Header.Id], ct);

        // Act
        var (exit, console) = await SweepAsync(ct, "--dry-run");

        // Assert
        new { Exit = exit, Lines = console.Stdout.Split('\n').Skip(1) }.Should().BeEquivalentTo(
            new
            {
                Exit = 0,
                Lines = new[]
                {
                    $"waiting {job.Header.Id.Value} home-laptop still running, last seen 2026-11-24T20:00:00Z",
                    "dry run: sweep: 0 re-queued, 0 exhausted, 0 rejected, 0 archived",
                    string.Empty,
                },
            },
            o => o.WithStrictOrdering(),
            console.Stderr);
    }

    // AC-11: the sweep leaves the listed claim untouched, then re-queues it once the registry no longer lists it.
    [Fact]
    public async Task Sweep_ListedThenNoLongerListed_WaitsThenRequeues()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model job = await SeedClaimAsync(ct);
        await SeedRegistryAsync("0.30.1", [job.Header.Id], ct);
        int before = await _harness.CommitCountOnMainAsync(ct);
        var (_, waiting) = await SweepAsync(ct);
        int afterWaiting = await _harness.CommitCountOnMainAsync(ct);
        await SeedRegistryAsync("0.30.1", [], ct);

        // Act
        var (exit, console) = await SweepAsync(ct);

        // Assert
        new
        {
            WaitingCommits = afterWaiting - before,
            WaitingLine = waiting.Stdout.StartsWith("waiting " + job.Header.Id.Value + " home-laptop still running", StringComparison.Ordinal),
            Exit = exit,
            console.Stdout,
            Claimed = await Bus.ExistsOnMainAsync(ClaimedPath(job), ct),
        }.Should().BeEquivalentTo(new
        {
            WaitingCommits = 0,
            WaitingLine = true,
            Exit = 0,
            Stdout = $"requeued {job.Header.Id.Value} home-laptop attempt 2\nsweep: 1 re-queued, 0 exhausted, 0 rejected, 0 archived\n",
            Claimed = false,
        });
    }

    // AC-11: a 0.30.0 node never writes running; its stale claim is re-queued exactly as today.
    [Fact]
    public async Task Sweep_0300NodeWithoutTheKey_RequeuedAsToday()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model job = await SeedClaimAsync(ct);
        await SeedRegistryAsync("0.30.0", [], ct);

        // Act
        var (exit, console) = await SweepAsync(ct);

        // Assert
        new { Exit = exit, console.Stdout }.Should().BeEquivalentTo(
            new { Exit = 0, Stdout = $"requeued {job.Header.Id.Value} home-laptop attempt 2\nsweep: 1 re-queued, 0 exhausted, 0 rejected, 0 archived\n" },
            console.Stderr);
    }

    // Slice 6 verify: the bound counts from the commit that added the claim; a later commit touching the claimed file (the node
    // re-committing it) does not move the claim time and so never holds the claim past 24 h + 15 min.
    [Fact]
    public async Task Sweep_LaterCommitTouchingTheClaim_DoesNotExtendTheBound()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Model job = await NewJobAsync(ct);
        await Bus.SeedRawFileAsync(ClaimedPath(job), EnvelopeWriter.Serialize(job), "acme claim " + job.Header.Id.Value + " home-laptop", now.AddHours(-26), ct);
        Model touched = await NewJobAsync(ct, job.Header.Id, "Read README.md again.\n");
        await Bus.SeedRawFileAsync(ClaimedPath(job), EnvelopeWriter.Serialize(touched), "acme claim " + job.Header.Id.Value + " home-laptop", now.AddMinutes(-10), ct);
        await SeedRegistryAsync("0.30.1", [job.Header.Id], ct);

        // Act
        var (exit, console) = await SweepAsync(ct);

        // Assert
        new { Exit = exit, console.Stdout }.Should().BeEquivalentTo(
            new { Exit = 0, Stdout = $"requeued {job.Header.Id.Value} home-laptop attempt 2\nsweep: 1 re-queued, 0 exhausted, 0 rejected, 0 archived\n" },
            console.Stderr);
    }

    // Spec 61 §3 (slice 6 correction): `zyggy status --id` names a claim its machine lists under running, with the registry's last_seen.
    [Fact]
    public async Task StatusId_ClaimListedAsRunning_ShowsRunningLastSeen()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model job = await SeedClaimAsync(ct);
        await SeedRegistryAsync("0.30.1", [job.Header.Id], ct);

        // Act
        var (exit, console) = await _harness.RunAsync(["status", "--id", job.Header.Id.Value], _harness.CentralNodeJson, ct: ct);

        // Assert
        new { Exit = exit, State = console.Stdout.Split('\n')[1] }.Should().BeEquivalentTo(
            new { Exit = 0, State = "state   claimed by home-laptop, running (last seen 2026-11-24T20:00:00Z)" },
            console.Stderr);
    }

    // Spec 61 §3 (slice 6 correction): a 0.30.0 target (no key) or a registry that does not list the id keeps today's line.
    [Theory]
    [InlineData("0.30.0")]
    [InlineData("0.30.1")]
    public async Task StatusId_ClaimNotListed_ShowsTodaysLine(string version)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model job = await SeedClaimAsync(ct);
        await SeedRegistryAsync(version, version == "0.30.1" ? [EnvelopeId.New(DateTimeOffset.UtcNow)] : [], ct);

        // Act
        var (exit, console) = await _harness.RunAsync(["status", "--id", job.Header.Id.Value], _harness.CentralNodeJson, ct: ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        console.Stdout.Split('\n')[1].Should().MatchRegex(@"^state   claimed by home-laptop since \d+ (s|min|h|d) ago$");
    }

    private static string ClaimedPath(Model job) => BusPaths.EnvelopeFile(BusPaths.Claimed(CliHarness.Acme, CliHarness.HomeLaptop), job.Header.Id);

    private ServiceProvider BuildNode() =>
        TestHost.BuildRunnerListener(
            Bus,
            CliHarness.HomeLaptop,
            _harness.Store,
            FakeClaude.ExecutablePath,
            TestHost.NodeConfigurationFor(Bus, CliHarness.HomeLaptop),
            o =>
            {
                o.NodeDir = _harness.NodeDirOf("home-laptop-listener");
                o.TimeoutOverride = TimeSpan.FromSeconds(30);
                o.ModelEnvironment = new Dictionary<string, string> { ["ZYGGY_FAKE_CLAUDE_SCENARIO"] = "done" };
            },
            extend: s => s.AddSingleton<TimeProvider>(_clock));

    private Task<(int Exit, VerbConsole Console)> SweepAsync(CancellationToken ct, params string[] more) =>
        _harness.RunAsync(["sweep", .. more], _harness.CentralNodeJson, more: s => s.AddSingleton<TimeProvider>(new OffsetClock(Ahead)), ct: ct);

    private async Task SeedRegistryAsync(string version, IReadOnlyList<EnvelopeId> running, CancellationToken ct)
    {
        var document = new RegistryDocument(CliHarness.Acme, CliHarness.HomeLaptop, RegistryOs.Windows, Seen, HealthLevel.Green, null, []) { Version = version, Running = running };
        await Bus.SeedRawFileAsync(RegistryPath, document.Serialize(), "acme registry home-laptop", ct);
    }

    private async Task<Model> SeedQueuedJobAsync(CancellationToken ct)
    {
        Model job = await NewJobAsync(ct);
        string queued = BusPaths.EnvelopeFile(BusPaths.Jobs(CliHarness.Acme, CliHarness.HomeLaptop), job.Header.Id);
        await Bus.SeedRawFileAsync(queued, EnvelopeWriter.Serialize(job), "acme job " + job.Header.Id.Value + " central→home-laptop", ct);
        return job;
    }

    private async Task<Model> SeedClaimAsync(CancellationToken ct)
    {
        Model job = await NewJobAsync(ct);
        await Bus.SeedRawFileAsync(ClaimedPath(job), EnvelopeWriter.Serialize(job), "acme claim " + job.Header.Id.Value + " home-laptop", ct);
        return job;
    }

    private Task<Model> NewJobAsync(CancellationToken ct, EnvelopeId? id = null, string body = "Read README.md and finish with a REPORT section; change nothing.\n") =>
        TestEnvelopes.SignedJobAsync(
            CliHarness.Central,
            CliHarness.HomeLaptop,
            _harness.Store,
            TestKeys.Acme1,
            body,
            ct,
            id: id,
            fields: new JobFields(_project.Path, TimeoutMinutes: 2, AllowedTools: ["Read"], Attempt: 1));

    private async Task<string> CommitAsync(string grep, CancellationToken ct) =>
        (await Bus.RunGitAsync(Bus.BareDir, ["log", "-1", "--format=%H", "--fixed-strings", grep, "main"], ct)).Trim();

    // Waits until the loop has polled once, then a moment more, so it sits in its wait for the next tick.
    private static async Task WaitForFirstPollAsync(BusListener listener, CancellationToken ct)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (listener.Status.LastPollUtc is null && waited.Elapsed < TimeSpan.FromSeconds(30))
        {
            await Task.Delay(50, ct);
        }

        await Task.Delay(500, ct);
    }

    // Polls main's registry for up to 60 s until its last_seen is after threshold; returns the last value read.
    private async Task<DateTimeOffset?> WaitForLastSeenAfterAsync(DateTimeOffset threshold, CancellationToken ct)
    {
        DateTimeOffset? lastSeen = null;
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (!(lastSeen > threshold) && waited.Elapsed < TimeSpan.FromSeconds(60))
        {
            await Task.Delay(250, ct);
            lastSeen = (await RegistryAtAsync("main", ct)).LastSeen;
        }

        return lastSeen;
    }

    private async Task<RegistryDocument> RegistryAtAsync(string revision, CancellationToken ct)
    {
        RegistryParse parse = RegistryDocument.Parse(await Bus.RunGitBytesAsync(Bus.BareDir, ["show", revision + ":" + RegistryPath], ct));
        parse.IsOk.Should().BeTrue(parse.Detail);
        return parse.Document!;
    }

    /// <summary>The system clock moved ahead by a fixed amount; timers stay real.</summary>
    private sealed class OffsetClock(TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + offset;
    }

    /// <summary>The system clock moved by <see cref="Jump"/>: the wall clock jumps, the timers stay real (a sleeping machine's do not run).</summary>
    private sealed class JumpClock : TimeProvider
    {
        private long _offsetTicks;

        public void Jump(TimeSpan span) => Interlocked.Add(ref _offsetTicks, span.Ticks);

        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));
    }
}
