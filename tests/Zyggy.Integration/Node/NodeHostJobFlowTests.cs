using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Jobs;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;
using Zyggy.Node;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Node;

/// <summary>Spec 07 AC-17..AC-20 through the production host on the bare repository with the fake <c>claude</c>: concurrency and deferral, work hours, the lying doorbell, stop and resume.</summary>
public sealed class NodeHostJobFlowTests
{
    [Fact]
    public async Task TwoJobsTwoProjectsThenThirdToFirst_ConcurrentThenDeferredThenThreeReports()
    {
        // Arrange: AC-17 — three jobs on the bus before the host starts: two for the first project, one for the second; three slots so
        // the second job of the first project is deferred for its project, not for a slot (the §6 rule order). The ids share a timestamp,
        // so which of the two first-project jobs runs first is the ULID's random part — the facts are written for either order.
        var ct = TestContext.Current.CancellationToken;
        await using NodeHostHarness harness = await NodeHostHarness.CreateAsync(ct);
        harness.WriteNodeJson(maxConcurrentJobs: 3);
        harness.FakeEnvironment["ZYGGY_FAKE_CLAUDE_DELAY_MS"] = "8000";
        ScratchProject first = await harness.ProjectAsync(ct);
        ScratchProject second = await harness.ProjectAsync(ct);
        SeededJob job1 = await harness.SeedJobAsync(first, ct);
        SeededJob job2 = await harness.SeedJobAsync(second, ct);
        SeededJob job3 = await harness.SeedJobAsync(first, ct);
        using IHost host = harness.Build();
        await host.StartAsync(ct);
        HealthServer server = host.Services.GetRequiredService<HealthServer>();
        await NodeHostHarness.WaitUntilAsync(() => Task.FromResult(server.IsListening), TimeSpan.FromSeconds(10), ct);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.BoundPort}/") };

        // Act 1: two run at once (one per project), the third waits for its project
        await NodeHostHarness.WaitUntilAsync(async () => (await HealthAsync(client, ct)).GetProperty("jobsRunning").GetInt32() == 2, TimeSpan.FromSeconds(45), ct);
        // jobsRunning counts a run once it is admitted; the fake claude writes its capture when the process has started, which a slow
        // runner can take a few seconds to do. 6 s stays inside the 8 s delay, so both still run at once.
        bool bothCapturesExist = await CapturesWithinAsync([first, second], TimeSpan.FromSeconds(6), ct);
        int reportsWhileBothRun = (await harness.ReportsOnMainAsync(ct)).Count;
        string? deferredLine = harness.LogLines.FirstOrDefault(l => l.Contains("deferred: ProjectBusy", StringComparison.Ordinal));
        SeededJob deferred = deferredLine?.Contains(job3.Id.Value, StringComparison.Ordinal) == true ? job3 : job1;
        bool deferredStillQueued = await harness.Bus.ExistsOnMainAsync(deferred.RelativePath, ct);

        // Act 2: all three end
        await NodeHostHarness.WaitUntilAsync(async () => (await harness.ReportsOnMainAsync(ct)).Count == 3, TimeSpan.FromSeconds(90), ct);
        await NodeHostHarness.WaitUntilAsync(async () =>
        {
            JsonElement health = await HealthAsync(client, ct);
            return health.GetProperty("jobsRunning").GetInt32() == 0 && health.GetProperty("jobsClaimedByMe").GetInt32() == 0;
        }, TimeSpan.FromSeconds(45), ct);
        await host.StopAsync(ct);

        // Assert
        bothCapturesExist.Should().BeTrue();
        reportsWhileBothRun.Should().Be(0);
        deferredLine.Should().NotBeNull("one of the first project's jobs waits for the other").And.Contain($"job {deferred.Id.Value} deferred: ProjectBusy");
        deferredStillQueued.Should().BeTrue("the deferred job stays in jobs/ on main");
        harness.LogLines.Where(l => l.Contains("deferred: ProjectBusy", StringComparison.Ordinal)).Should().ContainSingle();
        harness.LogLines.Where(l => l.Contains($"job {deferred.Id.Value} admitted after deferral", StringComparison.Ordinal)).Should().ContainSingle();
        IReadOnlyList<string> reports = await harness.ReportsOnMainAsync(ct);
        var replies = new List<EnvelopeId>();
        foreach (string path in reports)
        {
            Model report = await harness.ReportAsync(path, ct);
            report.Report!.Status.Should().Be(ReportStatus.Done);
            replies.Add(report.Header.InReplyTo!);
        }

        replies.Should().BeEquivalentTo([job1.Id, job2.Id, job3.Id]);
        SecretSweep.AssertAbsent(harness.LogLines);
        SecretSweep.AssertAbsent(harness.Hooks.Calls.SelectMany(SecretSweep.TextsOf));
        ProcessProbe.ProcessesWithWorkingDirectory(first.Path).Should().Be(0);
        ProcessProbe.ProcessesWithWorkingDirectory(second.Path).Should().Be(0);
    }

    [Fact]
    public async Task OutsideWorkHours_HighRunsNormalWaitsIdleInterval()
    {
        // Arrange: AC-18 — a one-hour UTC window on today's weekday that excludes the current hour
        var ct = TestContext.Current.CancellationToken;
        await using NodeHostHarness harness = await NodeHostHarness.CreateAsync(ct);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        int from = now.Hour <= 21 ? now.Hour + 1 : 0;
        string day = now.DayOfWeek.ToString()[..3];
        string window = string.Create(CultureInfo.InvariantCulture, $"{{ \"timezone\": \"Etc/UTC\", \"days\": [\"{day}\"], \"from\": \"{from:00}:00\", \"to\": \"{from + 1:00}:00\" }}");
        harness.WriteNodeJson(workHoursJson: window, activeSeconds: 1, idleSeconds: 60);
        ScratchProject project = await harness.ProjectAsync(ct);
        ScratchProject other = await harness.ProjectAsync(ct);
        SeededJob normal = await harness.SeedJobAsync(project, ct);
        SeededJob high = await harness.SeedJobAsync(other, ct, EnvelopePriority.High);
        using IHost host = harness.Build();

        // Act
        await host.StartAsync(ct);
        await NodeHostHarness.WaitUntilAsync(async () => (await harness.ReportsOnMainAsync(ct)).Count == 1, TimeSpan.FromSeconds(20), ct);
        NodeStatus status = host.Services.GetRequiredService<NodeStatus>();
        await NodeHostHarness.WaitUntilAsync(() => Task.FromResult(status.Listener?.Status.LastPollUtc is not null), TimeSpan.FromSeconds(10), ct);
        ListenerStatus listener = status.Listener!.Status;
        await host.StopAsync(ct);

        // Assert
        Model report = await harness.ReportAsync((await harness.ReportsOnMainAsync(ct)).Single(), ct);
        report.Header.InReplyTo.Should().Be(high.Id);
        (await harness.Bus.ExistsOnMainAsync(normal.RelativePath, ct)).Should().BeTrue("a normal job waits for the window");
        harness.LogLines.Where(l => l.Contains($"job {normal.Id.Value} deferred: OutsideWorkHours", StringComparison.Ordinal)).Should().ContainSingle(string.Join(Environment.NewLine, harness.LogLines));
        listener.Mode.Should().Be(PollMode.Idle);
        (listener.Poll.NextDueUtc - listener.LastPollUtc!.Value).Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(55)).And.BeLessThanOrEqualTo(TimeSpan.FromSeconds(65));
    }

    [Fact]
    public async Task DoorbellFrozen_SafetyFetchAfterOneMinuteClaimsAndReports()
    {
        // Arrange: AC-19 — the doorbell lies after the first tick; the one-minute safety fetch must catch the push
        var ct = TestContext.Current.CancellationToken;
        await using NodeHostHarness harness = await NodeHostHarness.CreateAsync(ct);
        ScratchProject project = await harness.ProjectAsync(ct);
        using IHost host = harness.Build();
        await host.StartAsync(ct);
        await NodeHostHarness.WaitUntilAsync(() => Task.FromResult(harness.Head.Requests.Count >= 1), TimeSpan.FromSeconds(10), ct);
        harness.Head.Freeze = true;
        SeededJob job = await harness.SeedJobAsync(project, ct);
        var stopwatch = Stopwatch.StartNew();

        // Act
        await NodeHostHarness.WaitUntilAsync(async () => (await harness.ReportsOnMainAsync(ct)).Count == 1, TimeSpan.FromSeconds(75), ct);
        stopwatch.Stop();
        await host.StopAsync(ct);

        // Assert
        harness.LogLines.Where(l => l.Contains("safety fetch: branch moved while the doorbell said unchanged; resynchronising", StringComparison.Ordinal)).Should().ContainSingle();
        (await harness.ReportAsync((await harness.ReportsOnMainAsync(ct)).Single(), ct)).Header.InReplyTo.Should().Be(job.Id);
        stopwatch.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(30), "the doorbell never announced the push; only the safety fetch did");
    }

    [Fact]
    public async Task StopDuringHangRun_KilledClaimStaysNoReportLocksReleasedStoppingLogged_NextHostResumes()
    {
        // Arrange: AC-20 — a run that hangs after partial output
        var ct = TestContext.Current.CancellationToken;
        await using NodeHostHarness harness = await NodeHostHarness.CreateAsync(ct);
        harness.Scenario = "no-result";
        harness.FakeEnvironment["ZYGGY_FAKE_CLAUDE_LINGER_MS"] = "60000";
        ScratchProject project = await harness.ProjectAsync(ct);
        SeededJob job = await harness.SeedJobAsync(project, ct);
        string claimedPath = BusPaths.EnvelopeFile(BusPaths.Claimed(NodeHostHarness.Acme, NodeHostHarness.HomeLaptop), job.Id);
        IHost host = harness.Build();
        await host.StartAsync(ct);
        NodeStatus status = host.Services.GetRequiredService<NodeStatus>();
        await NodeHostHarness.WaitUntilAsync(() => Task.FromResult(status.Listener?.Status.JobsRunning == 1), TimeSpan.FromSeconds(45), ct);
        await NodeHostHarness.WaitUntilAsync(() => Task.FromResult(File.Exists(Path.Combine(project.Path, "capture.bin"))), TimeSpan.FromSeconds(30), ct);

        // Act 1: stop while the run hangs
        var stopwatch = Stopwatch.StartNew();
        await host.StopAsync(ct);
        stopwatch.Stop();
        host.Dispose();

        // Assert 1
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20));
        (await harness.Bus.ExistsOnMainAsync(claimedPath, ct)).Should().BeTrue("the claim stays for the next start");
        (await harness.ReportsOnMainAsync(ct)).Should().BeEmpty();
        ProjectLock.IsHeld(project.Path).Should().BeFalse();
        File.Exists(Path.Combine(harness.Bus.CloneDir, ".git", "zyggy-listen.lock")).Should().BeFalse();
        harness.LogLines.Where(l => l.Contains("stopping: 1 job(s) interrupted; their claims stay for the next start", StringComparison.Ordinal)).Should().ContainSingle();

        // Act 2: the next host resumes the claim
        harness.Scenario = "done";
        harness.FakeEnvironment.Clear();
        // generous: under full-suite load the second host's start-up, resume and fake run stretch; a passing run never waits this out
        int exitCode = await harness.RunUntilAsync(async () => (await harness.ReportsOnMainAsync(ct)).Count == 1, TimeSpan.FromSeconds(120), ct);

        // Assert 2
        exitCode.Should().Be(0);
        harness.LogLines.Where(l => l.Contains("resuming 1 claimed job(s) from a previous run", StringComparison.Ordinal)).Should().ContainSingle(string.Join(Environment.NewLine, harness.LogLines));
        Model report = await harness.ReportAsync((await harness.ReportsOnMainAsync(ct)).Single(), ct);
        report.Header.InReplyTo.Should().Be(job.Id);
        (await harness.Bus.ExistsOnMainAsync(claimedPath, ct)).Should().BeFalse();
        (await harness.CommitSubjectsAsync(ct)).Where(s => s.StartsWith("acme claim ", StringComparison.Ordinal)).Should().ContainSingle("the first host claimed it once; the resume never claims again");
        SecretSweep.AssertAbsent(harness.LogLines);
        SecretSweep.AssertAbsent(harness.Hooks.Calls.SelectMany(SecretSweep.TextsOf));
        ProcessProbe.ProcessesWithWorkingDirectory(project.Path).Should().Be(0, "the probe deletes the directory on Windows (plan A5), so it comes last");
    }

    private static async Task<JsonElement> HealthAsync(HttpClient client, CancellationToken ct)
    {
        using JsonDocument document = JsonDocument.Parse(await client.GetStringAsync(new Uri("/health", UriKind.Relative), ct));
        return document.RootElement.Clone();
    }

    private static async Task<bool> CapturesWithinAsync(IReadOnlyList<ScratchProject> projects, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            await NodeHostHarness.WaitUntilAsync(() => Task.FromResult(projects.All(p => File.Exists(Path.Combine(p.Path, "capture.bin")))), timeout, ct);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}
