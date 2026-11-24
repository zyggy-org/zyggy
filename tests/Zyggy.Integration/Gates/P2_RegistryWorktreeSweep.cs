// The P2 gate (founding spec §12), automated half. Parts and owners:
//   Part 1 (deliverable 14, this region): registry published, a job routed by name — `zyggy submit --project <name>` from central →
//           the real zyggy-node host resolves the name from its own scan → fake claude in the discovered folder → signed report on main.
//   Part 2 (deliverable 16, worktrees): `zyggy submit --project <name> --worktree` → the host runs the fake claude in
//           <nodeDir>/worktrees/acme/<id> on zyggy/<id>, pushes the branch to the project's own origin → `done` report whose
//           diff_ref is that branch's SHA on the origin.
//   Part 3 (deliverable 17, sweep): a node stopped while it runs a claimed job → `zyggy sweep` on Central, its clock past the limit,
//           re-queues it → the restarted host runs it → `done`; a claim stale at attempt 3 → Central's `attempts_exhausted` report shown
//           by `zyggy status --id`, and a 15-day-old report moved to archive/ in the same pass; `zyggy verify` clean.
// Rule: later parts append their own region and reuse the private helpers at the bottom; part 1 is never restructured.
// Nothing in the envelope wire format changed for this gate; PROTOCOL.md stays the placeholder (15 copies §4).
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Jobs;
using Zyggy.Core.Processes;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;
using Zyggy.Integration.Node;
using Zyggy.Node;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Gates;

/// <summary>
/// Phase P2 gate, part 1 (spec 14 AC-19, AC-24, AC-25): the registry on the bus and a job routed by registry name; part 2 (spec 16 AC-16):
/// a worktree job whose branch is pushed; part 3 (spec 17 AC-18): Central's sweep re-queues a stopped node's claim, ends a third stale
/// claim and archives old reports — all through the production host and the CLI.
/// </summary>
public sealed partial class P2_RegistryWorktreeSweep : IAsyncLifetime
{
    private const string Body = "Read README.md and finish with a REPORT section; change nothing.\n";

    private NodeHostHarness _harness = null!;

    public async ValueTask InitializeAsync() => _harness = await NodeHostHarness.CreateAsync(CancellationToken.None);

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    #region Part 1 — 14

    [Fact]
    public async Task Part1a_RegistryPublished_SubmitByName_DoneReportOnMain()
    {
        // Arrange: two discovered projects; the host is up, has polled and has published its registry
        var ct = TestContext.Current.CancellationToken;
        ScratchProject calizr = await _harness.ProjectAsync(ct, "calizr");
        await _harness.ProjectAsync(ct, "client-portal");
        using IHost host = await StartHostWithRegistryAsync(ct);

        // Act (a): `zyggy submit` from central, naming the project by its registry name
        var (submitExit, submit) = await CliAsync(
            ["submit", "--to", "home-laptop", "--project", "calizr", "--timeout", "2", "--allowed-tools", "Read", Body], ct);
        submitExit.Should().Be(0, submit.Stderr);
        Match submitted = SubmittedLine().Match(submit.Stdout);
        submitted.Success.Should().BeTrue(submit.Stdout);
        string id = submitted.Groups["id"].Value;

        // Act (b): the host claims it, runs the fake claude in the discovered folder and answers on main
        await NodeHostHarness.WaitUntilAsync(async () => (await _harness.ReportsOnMainAsync(ct)).Count == 1, TimeSpan.FromSeconds(60), ct);
        await host.StopAsync(ct);

        // Assert
        Model report = await SingleReportAsync(ct);
        report.Header.InReplyTo!.Value.Should().Be(id);
        report.Report!.Status.Should().Be(ReportStatus.Done, Encoding.UTF8.GetString(report.Body.Span));
        File.Exists(Path.Combine(calizr.Path, "capture.bin")).Should().BeTrue("the fake claude ran in the folder the node discovered");
        var (verifyExit, verify) = await CliAsync(["verify"], ct, _harness.NodeJsonPath);
        verifyExit.Should().Be(0, verify.Stdout + verify.Stderr);

        // Oldest first: the registry, then the job, its claim and its report.
        string[] subjects = [.. (await _harness.CommitSubjectsAsync(ct)).Reverse()];
        int registry = Array.IndexOf(subjects, "acme registry home-laptop");
        int job = Array.IndexOf(subjects, $"acme job {id} central→home-laptop");
        int claim = Array.IndexOf(subjects, $"acme claim {id} central→home-laptop");
        int reported = Array.FindIndex(subjects, s => s.StartsWith("acme report ", StringComparison.Ordinal) && s.EndsWith(" home-laptop→central", StringComparison.Ordinal));
        registry.Should().BeGreaterThanOrEqualTo(0, string.Join('\n', subjects));
        job.Should().BeGreaterThan(registry);
        claim.Should().BeGreaterThan(job);
        reported.Should().BeGreaterThan(claim);
        SweepSecrets(submit, verify);
    }

    [Fact]
    public async Task Part1b_SubmitUnknownName_RejectedUnknownProject_ModelNeverStarted()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await _harness.ProjectAsync(ct, "calizr");
        using IHost host = await StartHostWithRegistryAsync(ct);

        // Act
        var (submitExit, submit) = await CliAsync(
            ["submit", "--to", "home-laptop", "--project", "no-such-project", "--timeout", "2", "--allowed-tools", "Read", Body], ct);
        submitExit.Should().Be(0, submit.Stderr);
        string id = SubmittedLine().Match(submit.Stdout).Groups["id"].Value;
        await NodeHostHarness.WaitUntilAsync(async () => (await _harness.ReportsOnMainAsync(ct)).Count == 1, TimeSpan.FromSeconds(60), ct);
        await host.StopAsync(ct);

        // Assert
        Model report = await SingleReportAsync(ct);
        report.Header.InReplyTo!.Value.Should().Be(id);
        report.Report!.Status.Should().Be(ReportStatus.Rejected);
        report.Report.Reason.Should().Be("unknown_project");
        Encoding.UTF8.GetString(report.Body.Span).Should().Be("unknown_project: unknown name\n");
        Directory.EnumerateFiles(_harness.ProjectsRoot, "capture.bin", SearchOption.AllDirectories).Should().BeEmpty("the model never started");
        File.Exists(TranscriptPaths.For(_harness.NodeDir, NodeHostHarness.Acme, report.Header.InReplyTo)).Should().BeFalse();
        SweepSecrets(submit);
    }

    #endregion

    #region Part 2 — 16

    [Fact]
    public async Task Part2_SubmitWorktreeByName_BranchPushedAndDiffRefOnProjectOrigin()
    {
        // Arrange: a discovered project with one commit pushed to its own bare origin; the fake model adds a file
        var ct = TestContext.Current.CancellationToken;
        ScratchProject shop = await _harness.ProjectAsync(ct, "shop");
        string origin = await WithOriginAsync(shop, ct);
        _harness.FakeEnvironment["ZYGGY_FAKE_CLAUDE_EDITS"] = "+src/feature.txt\tfeature";
        using IHost host = await StartHostWithRegistryAsync(ct);

        // Act (a): `zyggy submit --worktree` from central, by registry name
        var (submitExit, submit) = await CliAsync(
            ["submit", "--to", "home-laptop", "--project", "shop", "--worktree", "--timeout", "2", "--allowed-tools", "Read,Edit", "Add the feature file.\n"], ct);
        submitExit.Should().Be(0, submit.Stderr);
        string id = SubmittedLine().Match(submit.Stdout).Groups["id"].Value;

        // Act (b): the host runs it in a worktree, pushes zyggy/<id> to the project's origin and answers on main
        await NodeHostHarness.WaitUntilAsync(async () => (await _harness.ReportsOnMainAsync(ct)).Count == 1, TimeSpan.FromSeconds(60), ct);
        await host.StopAsync(ct);

        // Assert
        Model report = await SingleReportAsync(ct);
        report.Header.InReplyTo!.Value.Should().Be(id);
        report.Report!.Status.Should().Be(ReportStatus.Done, Encoding.UTF8.GetString(report.Body.Span));
        report.Report.FilesChanged.Should().Equal("src/feature.txt");
        report.Report.DiffRef.Should().Be(await _harness.Bus.RunGitAsync(origin, ["rev-parse", "refs/heads/zyggy/" + id], ct));
        (await _harness.Bus.RunGitAsync(shop.Path, ["symbolic-ref", "--short", "HEAD"], ct)).Should().Be("main", "the checkout is untouched");
        var (verifyExit, verify) = await CliAsync(["verify"], ct, _harness.NodeJsonPath);
        verifyExit.Should().Be(0, verify.Stdout + verify.Stderr);
        SweepSecrets(submit, verify);
    }

    /// <summary>Gives <paramref name="project"/> one commit (with <c>capture.bin</c>, the harness's fake capture, ignored) pushed to a new bare origin; returns the origin.</summary>
    private async Task<string> WithOriginAsync(ScratchProject project, CancellationToken ct)
    {
        string origin = _harness.Scratch.File(Path.GetFileName(project.Path) + "-origin.git");
        await _harness.Bus.RunGitAsync(_harness.Scratch.Path, ["init", "-q", "--bare", "--initial-branch=main", origin], ct);
        await File.WriteAllTextAsync(Path.Combine(project.Path, ".gitignore"), "capture.bin\n", ct);
        foreach (string[] args in (string[][])
                 [
                     ["config", "user.name", "alice (fixture)"], ["config", "user.email", "alice@example.invalid"], ["config", "commit.gpgsign", "false"],
                     ["add", "-A"], ["commit", "-q", "-m", "initial"], ["remote", "add", "origin", origin], ["push", "-q", "origin", "main"],
                     ["remote", "set-head", "origin", "main"],
                 ])
        {
            await _harness.Bus.RunGitAsync(project.Path, args, ct);
        }

        return origin;
    }

    #endregion

    #region Part 3 — 17

    // AC-18: a job claimed by a node that is then stopped is re-queued by Central's pass once the clock is past the limit, and the
    // restarted node runs it and reports done.
    [Fact]
    public async Task Part3a_ClaimOfStoppedNode_RequeuedByCentralRunAgainDone()
    {
        // Arrange: the node claims the job and is stopped while the model still runs
        var ct = TestContext.Current.CancellationToken;
        await _harness.ProjectAsync(ct, "calizr");
        _harness.FakeEnvironment["ZYGGY_FAKE_CLAUDE_DELAY_MS"] = "600000";
        string id;
        using (IHost first = await StartHostWithRegistryAsync(ct))
        {
            var (submitExit, submit) = await CliAsync(
                ["submit", "--to", "home-laptop", "--project", "calizr", "--timeout", "2", "--allowed-tools", "Read", Body], ct);
            submitExit.Should().Be(0, submit.Stderr);
            string submitted = SubmittedLine().Match(submit.Stdout).Groups["id"].Value;
            await NodeHostHarness.WaitUntilAsync(() => _harness.Bus.ExistsOnMainAsync(ClaimedPath(submitted), ct), TimeSpan.FromSeconds(60), ct);
            await first.StopAsync(ct);
            id = submitted;
        }

        _harness.FakeEnvironment.Remove("ZYGGY_FAKE_CLAUDE_DELAY_MS");

        // Act (a): Central's pass puts the claim back in the queue. Spec 61 slice 6: the stopped node's registry still lists the job under
        // running, so the sweep waits for it up to the longest a run can live (24 h + 15 min); Central's clock runs 25 hours ahead, past it.
        var (sweepExit, sweep) = await CliAsync(["sweep"], ct, clock: new OffsetClock(TimeSpan.FromHours(25)));
        sweepExit.Should().Be(0, sweep.Stderr);

        // Act (b): the restarted node claims it again, runs it and answers on main
        using IHost second = await StartHostWithRegistryAsync(ct);
        await NodeHostHarness.WaitUntilAsync(async () => (await _harness.ReportsOnMainAsync(ct)).Count == 1, TimeSpan.FromSeconds(60), ct);
        await second.StopAsync(ct);

        // Assert
        sweep.Stdout.Should().StartWith($"requeued {id} home-laptop attempt 2\n");
        Model report = await SingleReportAsync(ct);
        report.Header.InReplyTo!.Value.Should().Be(id);
        report.Report!.Status.Should().Be(ReportStatus.Done, Encoding.UTF8.GetString(report.Body.Span));
        (await _harness.CommitSubjectsAsync(ct)).Should().Contain($"acme requeue {id} central→home-laptop");
        var (verifyExit, verify) = await CliAsync(["verify"], ct, _harness.NodeJsonPath);
        verifyExit.Should().Be(0, verify.Stdout + verify.Stderr);
        SweepSecrets(sweep, verify);
    }

    // AC-18: a claim stale at attempt 3 ends in Central's attempts_exhausted report that `zyggy status --id` shows; a 15-day-old
    // report is archived in the same pass; `zyggy verify` stays clean.
    [Fact]
    public async Task Part3b_ThirdStaleClaim_ExhaustedShownByStatus_OldReportArchived_VerifyClean()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        ScratchProject calizr = await _harness.ProjectAsync(ct, "calizr");
        Model job = await TestEnvelopes.SignedJobAsync(
            NodeHostHarness.Central, NodeHostHarness.HomeLaptop, _harness.Store, TestKeys.Acme1, Body, ct,
            fields: new JobFields(calizr.Path, TimeoutMinutes: 2, AllowedTools: ["Read"]));
        EnvelopeResult third = await new EnvelopeSigner(_harness.Store).SignAsync(job.WithAttempt(3), TestKeys.Acme1, ct);
        string id = job.Header.Id.Value;
        await _harness.Bus.SeedRawFileAsync(ClaimedPath(id), EnvelopeWriter.Serialize(third.Envelope!), $"acme claim {id} home-laptop", ct);
        DateTimeOffset old = new(DateTime.UtcNow.Date.AddDays(-15).AddHours(12), TimeSpan.Zero);
        SeededJob oldReport = await _harness.Bus.SeedSignedReportAsync(
            NodeHostHarness.HomeLaptop, MachineName.Parse("work-laptop"), EnvelopeId.New(old), TestKeys.Acme1, _harness.Store, "done\n", created: old, ct: ct);
        var (sweepExit, sweep) = await CliAsync(["sweep"], ct, clock: new OffsetClock(TimeSpan.FromHours(1)));
        sweepExit.Should().Be(0, sweep.Stderr);

        // Act
        var (statusExit, status) = await CliAsync(["status", "--id", id], ct);

        // Assert
        statusExit.Should().Be(0, status.Stderr);
        status.Stdout.Should().Contain("failed attempts_exhausted");
        sweep.Stdout.Should().StartWith($"exhausted {id} home-laptop after 3 attempts\narchived 1 file(s), oldest created ");
        (await _harness.Bus.ExistsOnMainAsync(ClaimedPath(id), ct)).Should().BeFalse();
        (await _harness.Bus.ExistsOnMainAsync(oldReport.RelativePath, ct)).Should().BeFalse();
        (await _harness.Bus.ExistsOnMainAsync(BusPaths.EnvelopeFile(BusPaths.Archive(NodeHostHarness.Acme, old.Year, old.Month), oldReport.Id), ct)).Should().BeTrue();
        var (verifyExit, verify) = await CliAsync(["verify"], ct, _harness.NodeJsonPath);
        verifyExit.Should().Be(0, verify.Stdout + verify.Stderr);
        SweepSecrets(sweep, status, verify);
    }

    private static string ClaimedPath(string id) =>
        BusPaths.EnvelopeFile(BusPaths.Claimed(NodeHostHarness.Acme, NodeHostHarness.HomeLaptop), EnvelopeId.Parse(id));

    /// <summary>The system clock moved ahead by a fixed amount; timers stay real.</summary>
    private sealed class OffsetClock(TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + offset;
    }

    #endregion

    // ----- helpers shared by every part -----

    /// <summary>Starts the production host and waits until it has polled and its registry file is on <c>main</c>.</summary>
    private async Task<IHost> StartHostWithRegistryAsync(CancellationToken ct)
    {
        IHost host = _harness.Build();
        await host.StartAsync(ct);
        HealthServer server = host.Services.GetRequiredService<HealthServer>();
        await NodeHostHarness.WaitUntilAsync(() => Task.FromResult(server.IsListening), TimeSpan.FromSeconds(60), ct);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.BoundPort}/") };
        await NodeHostHarness.WaitUntilAsync(async () =>
        {
            using JsonDocument health = JsonDocument.Parse(await client.GetStringAsync(new Uri("/health", UriKind.Relative), ct));
            return health.RootElement.GetProperty("lastPollUtc").ValueKind == JsonValueKind.String
                && await _harness.RegistryOnMainAsync(NodeHostHarness.HomeLaptop, ct) is not null;
        }, TimeSpan.FromSeconds(60), ct);
        return host;
    }

    /// <summary>A <c>node.json</c> for <c>central</c> over the sender clone, signing with <c>acme/1</c> from the same file secret store.</summary>
    private string CentralNodeJson() =>
        NodeJson.Write(
            _harness.Scratch.File("central-config"),
            NodeHostHarness.Acme,
            NodeHostHarness.Central,
            TestKeys.Acme1,
            busLocalPath: _harness.Bus.SenderDir,
            remote: TestHost.Remote.HttpsUrl!.ToString(),
            secretsRoot: _harness.SecretsRoot,
            claudePath: FakeClaude.ExecutablePath,
            nodeDir: _harness.Scratch.File("node-central"));

    /// <summary>Runs a verb in process — as <c>central</c> unless <paramref name="nodeJson"/> says otherwise — with the harness's edges (the doorbell from the bare repository, recorded processes).</summary>
    private Task<(int Exit, VerbConsole Console)> CliAsync(string[] args, CancellationToken ct, string? nodeJson = null, TimeProvider? clock = null)
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["ZYGGY_NODE_JSON"] = nodeJson ?? CentralNodeJson() };
        return ZyggyCliInProcess.RunAsync(args, environment, services =>
        {
            CliServices(services);
            if (clock is not null)
            {
                services.AddSingleton(clock);
            }
        }, stdin: null, ct);
    }

    private void CliServices(IServiceCollection services)
    {
        services.Configure<GitHubBusOptions>(o =>
        {
            o.HandlerFactory = () => new BareRepoHeadHandler(async c => await _harness.Bus.HeadShaAsync(c));
            o.GitEnvironment = _harness.Bus.GitEnvironment;
            o.Jitter = () => 0;
        });
        services.AddSingleton<IProcessRunner>(_harness.Hooks);
    }

    private async Task<Model> SingleReportAsync(CancellationToken ct) =>
        await _harness.ReportAsync((await _harness.ReportsOnMainAsync(ct)).Single(), ct);

    private void SweepSecrets(params VerbConsole[] consoles)
    {
        SecretSweep.AssertAbsent(consoles.SelectMany(c => new[] { c.Stdout, c.Stderr }));
        SecretSweep.AssertAbsent(_harness.LogLines);
        SecretSweep.AssertAbsent(_harness.Hooks.Calls.SelectMany(SecretSweep.TextsOf));
    }

    // Spec 50 slice 6 (AC-9): a submit without --type or --model is followed by the line `model: node default`.
    [GeneratedRegex(@"^submitted (?<id>[0-9A-HJKMNP-TV-Z]{26}) to home-laptop \(tenants/acme/nodes/home-laptop/jobs/\k<id>\.md\)\nmodel: node default\n\z")]
    private static partial Regex SubmittedLine();
}
