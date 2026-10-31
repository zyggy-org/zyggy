// The P1 walking-skeleton gate (founding spec §12), automated half. Parts and owners:
//   Part 1  (deliverable 07, this region): submit → real zyggy-node host (production DI, tenant acme) → fake claude → signed report on main,
//           opened by the ex-05 probe round trip, the tampered-job rejection and the foreign-tenant isolation.
//   Part 1b (deliverable 08, below): `zyggy submit` → the same host → `zyggy status` / `status --id` / `zyggy verify` in process over the same bus.
//   Part 2  (deliverable 13, below): `zyggy context` from home-laptop → the host as central picks it up → the Hub's get_context returns it; digest ≤ 20,000 bytes.
// Rule: later parts append their own region and reuse the private helpers at the bottom; part 1 is never restructured.
// Nothing in the wire format changed for this gate: PROTOCOL.md and the §4 envelope are as deliverables 03/04 left them.
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Jobs;
using Zyggy.Core.Processes;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;
using Zyggy.Integration.Node;
using Zyggy.Node;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Gates;

/// <summary>Phase P1 gate, part 1 (spec 07 AC-23) and part 1b (spec 08 AC-12): the walking skeleton's automated half through the production host and the CLI.</summary>
public sealed partial class P1_WalkingSkeleton : IAsyncLifetime
{
    private NodeHostHarness _harness = null!;

    public async ValueTask InitializeAsync() => _harness = await NodeHostHarness.CreateAsync(CancellationToken.None);

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    #region Part 1 — 07

    [Fact]
    public async Task Part1a_ProbeJobFromCentral_ClaimedAndAnsweredByTheRealHost()
    {
        // Arrange: the ex-05 probe round trip — a probe-shaped job (registry name "probe"). With the job runner as the handler the
        // pre-flight answers `rejected`/`unknown_project` by design until registry names resolve (deliverable 14 turns this into `done`);
        // the ex-05 "answered" criterion is the signed report, not its status (plan 07 A14).
        var ct = TestContext.Current.CancellationToken;
        SeededJob job = await _harness.Bus.SeedSignedJobAsync(NodeHostHarness.Central, NodeHostHarness.HomeLaptop, TestKeys.Acme1, _harness.Store, "probe\n", ct: ct);

        // Act
        await RunHostUntilReportsAsync(1, ct);

        // Assert
        Model report = await SingleReportAsync(ct);
        report.Header.InReplyTo.Should().Be(job.Id);
        report.Header.From.Should().Be(NodeHostHarness.HomeLaptop);
        report.Header.To.Should().Be(NodeHostHarness.Central);
        report.Report!.Status.Should().Be(ReportStatus.Rejected);
        report.Report.Reason.Should().Be("unknown_project");
        (await _harness.CommitSubjectsAsync(ct)).Should().Contain($"acme claim {job.Id.Value} central→home-laptop");
        (await _harness.Bus.ExistsOnMainAsync(BusPaths.EnvelopeFile(BusPaths.Claimed(NodeHostHarness.Acme, NodeHostHarness.HomeLaptop), job.Id), ct)).Should().BeFalse();
    }

    [Fact]
    public async Task Part1b_TamperedJob_RejectedOnMainWithReportModelNeverStarted()
    {
        // Arrange: one byte of the signature flipped
        var ct = TestContext.Current.CancellationToken;
        ScratchProject project = await _harness.ProjectAsync(ct);
        SeededJob job = await _harness.Bus.SeedSignedJobAsync(
            NodeHostHarness.Central, NodeHostHarness.HomeLaptop, TestKeys.Acme1, _harness.Store, "probe\n",
            mutate: bytes => FlipSignatureByte(bytes), fields: new JobFields(project.Path, TimeoutMinutes: 2), ct: ct);

        // Act
        await RunHostUntilReportsAsync(1, ct);

        // Assert
        Model report = await SingleReportAsync(ct);
        report.Header.InReplyTo.Should().Be(job.Id);
        report.Report!.Status.Should().Be(ReportStatus.Rejected);
        report.Report.Reason.Should().Be("invalid_signature");
        (await _harness.Bus.ExistsOnMainAsync(BusPaths.EnvelopeFile(BusPaths.Rejected(NodeHostHarness.Acme, NodeHostHarness.HomeLaptop), job.Id), ct)).Should().BeTrue();
        (await _harness.Bus.ExistsOnMainAsync(job.RelativePath, ct)).Should().BeFalse();
        File.Exists(Path.Combine(project.Path, "capture.bin")).Should().BeFalse("the model never started");
        File.Exists(TranscriptPaths.For(_harness.NodeDir, NodeHostHarness.Acme, job.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task Part1c_ForeignTenantFile_IgnoredAndUntouched()
    {
        // Arrange: a file of another tenant in the same checkout, next to one real job
        var ct = TestContext.Current.CancellationToken;
        var foreignId = EnvelopeId.New(DateTimeOffset.UtcNow);
        string foreignPath = "tenants/other-org/nodes/home-laptop/jobs/" + foreignId.Value + ".md";
        byte[] foreignBytes = Encoding.UTF8.GetBytes("---\ntenant: other-org\nid: " + foreignId.Value + "\n---\nnot ours\n");
        await _harness.Bus.SeedRawFileAsync(foreignPath, foreignBytes, "seed: a foreign tenant's job", ct);
        ScratchProject project = await _harness.ProjectAsync(ct);
        SeededJob job = await _harness.SeedJobAsync(project, ct);

        // Act: the real job's full round trip, which takes several ticks
        await RunHostUntilReportsAsync(1, ct);

        // Assert
        (await _harness.Bus.ShowBytesAsync(foreignPath, ct)).Should().Equal(foreignBytes, "byte-identical on main");
        (await _harness.Bus.ExistsOnMainAsync("tenants/other-org/nodes/home-laptop/jobs/claimed/" + foreignId.Value + ".md", ct)).Should().BeFalse();
        (await _harness.Bus.ExistsOnMainAsync("tenants/other-org/nodes/home-laptop/jobs/rejected/" + foreignId.Value + ".md", ct)).Should().BeFalse();
        Model report = await SingleReportAsync(ct);
        report.Header.InReplyTo.Should().Be(job.Id);
        (await _harness.CommitSubjectsAsync(ct)).Should().NotContain(s => s.Contains(foreignId.Value, StringComparison.Ordinal) && !s.StartsWith("seed:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Part1d_SubmitToReportOnMain_DoneWithMetricsTranscriptAndHealth()
    {
        // Arrange: submit → node → fake claude → report on main
        var ct = TestContext.Current.CancellationToken;
        ScratchProject project = await _harness.ProjectAsync(ct);
        SeededJob job = await _harness.SeedJobAsync(project, ct);
        using IHost host = _harness.Build();
        await host.StartAsync(ct);
        HealthServer server = host.Services.GetRequiredService<HealthServer>();

        // Act
        await NodeHostHarness.WaitUntilAsync(async () => server.IsListening && (await _harness.ReportsOnMainAsync(ct)).Count == 1, TimeSpan.FromSeconds(60), ct);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.BoundPort}/") };
        // the claimed count refreshes on the pull that follows the node's own report push (the next tick)
        await NodeHostHarness.WaitUntilAsync(async () =>
        {
            JsonElement h = await HealthAsync(client, ct);
            return h.GetProperty("jobsRunning").GetInt32() == 0 && h.GetProperty("jobsClaimedByMe").GetInt32() == 0;
        }, TimeSpan.FromSeconds(30), ct);
        string healthBody = await client.GetStringAsync(new Uri("/health", UriKind.Relative), ct);
        await host.StopAsync(ct);

        // Assert: the report
        Model report = await SingleReportAsync(ct);
        report.Header.InReplyTo.Should().Be(job.Id);
        report.Report!.Status.Should().Be(ReportStatus.Done);
        report.Report.CostUsd.Should().NotBeNull();
        report.Report.NumTurns.Should().NotBeNull();
        report.Report.Model.Should().NotBeNull();
        report.Report.FilesChanged.Should().BeEmpty();
        report.Report.DiffRef.Should().BeNull();
        report.BodyText.Should().Be(ExpectedReportSection(await ScenarioResultTextAsync("done", ct)) + "\n");
        (await File.ReadAllBytesAsync(TranscriptPaths.For(_harness.NodeDir, NodeHostHarness.Acme, job.Id), ct))
            .Should().Equal(await File.ReadAllBytesAsync(Path.Combine(FakeClaude.ScenarioDirectory, "done.jsonl"), ct));

        // Assert: /health
        using JsonDocument health = JsonDocument.Parse(healthBody);
        health.RootElement.GetProperty("tenant").GetString().Should().Be("acme");
        health.RootElement.GetProperty("machine").GetString().Should().Be("home-laptop");
        health.RootElement.GetProperty("lastReportUtc").ValueKind.Should().Be(JsonValueKind.String);
        health.RootElement.GetProperty("jobsRunning").GetInt32().Should().Be(0);
        health.RootElement.GetProperty("jobsClaimedByMe").GetInt32().Should().Be(0);

        // Assert: no secret anywhere
        SecretSweep.AssertAbsent(_harness.LogLines);
        SecretSweep.AssertAbsent([healthBody]);
        SecretSweep.AssertAbsent(_harness.Hooks.Calls.SelectMany(SecretSweep.TextsOf));
    }

    #endregion

    #region Part 1b — 08

    [Fact]
    public async Task Part1b_SubmitStatusVerify_HumanEntryAndReadOutOverTheRealHost()
    {
        // Arrange: the production host over the node clone, a scratch project, and central's node.json over the sender clone (spec 08 AC-12)
        var ct = TestContext.Current.CancellationToken;
        ScratchProject project = await _harness.ProjectAsync(ct);
        string centralNodeJson = CentralNodeJson();
        using IHost host = _harness.Build();
        await host.StartAsync(ct);
        HealthServer server = host.Services.GetRequiredService<HealthServer>();
        await NodeHostHarness.WaitUntilAsync(() => Task.FromResult(server.IsListening), TimeSpan.FromSeconds(10), ct);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.BoundPort}/") };
        await NodeHostHarness.WaitUntilAsync(async () =>
        {
            JsonElement h = await HealthAsync(client, ct);
            return h.GetProperty("lastPollUtc").ValueKind == JsonValueKind.String && h.GetProperty("claudeAuthOk").ValueKind == JsonValueKind.True;
        }, TimeSpan.FromSeconds(30), ct);
        string healthUrl = $"http://127.0.0.1:{server.BoundPort}/health";

        // Act (a): the human entry point — `zyggy submit` from central
        var (submitExit, submit) = await CliAsync(
            ["submit", "--to", "home-laptop", "--project", project.Path, "--timeout", "2", "--allowed-tools", "Read", "Read README.md and finish with a REPORT section; change nothing.\n"],
            centralNodeJson, healthUrl: null, ct);

        // Assert (a)
        submitExit.Should().Be(0, submit.Stderr);
        Match submitted = SubmittedLine().Match(submit.Stdout);
        submitted.Success.Should().BeTrue(submit.Stdout);
        string id = submitted.Groups["id"].Value;

        // Act (b): the host claims it, runs the fake claude and answers on main
        await NodeHostHarness.WaitUntilAsync(async () =>
        {
            if ((await _harness.ReportsOnMainAsync(ct)).Count != 1)
            {
                return false;
            }

            JsonElement h = await HealthAsync(client, ct);
            return h.GetProperty("jobsRunning").GetInt32() == 0 && h.GetProperty("jobsClaimedByMe").GetInt32() == 0;
        }, TimeSpan.FromSeconds(60), ct);

        // Assert (b)
        Model report = await SingleReportAsync(ct);
        report.Header.InReplyTo.Should().Be(EnvelopeId.Parse(id));
        report.Report!.Status.Should().Be(ReportStatus.Done);

        // Act (c): the read-out — `zyggy status` from home-laptop against the live health endpoint
        var (statusExit, status) = await CliAsync(["status"], _harness.NodeJsonPath, healthUrl, ct);

        // Assert (c): green over the §11 fields; the node's and the CLI's versions are the same informational version in this process (A17)
        statusExit.Should().Be(0, status.Stdout + status.Stderr);
        string[] lines = status.Stdout.Split('\n');
        lines[0].Should().StartWith("node    home-laptop (acme) · zyggy-node ").And.EndWith(" · health: green");
        lines[1].Should().Contain(" · claude auth ok");
        lines[2].Should().MatchRegex(@"^bus     head [0-9a-f]{12} · 1 tenant directory$");
        lines[3].Should().Be("        machine       queued  claimed  rejected  reports  context");
        lines[4].Should().Be("        central            0        0         0        0        0");
        lines[5].Should().Be("        home-laptop        0        0         0        1        0");
        lines[6].Should().Be("recent reports (1 of 1)");
        lines[7].Should().StartWith($"        {report.Header.Id.Value}  home-laptop→central  done  in_reply_to {id}  cost_usd ");

        // Act (d): one job traced
        var (traceExit, trace) = await CliAsync(["status", "--id", id], _harness.NodeJsonPath, healthUrl, ct);

        // Assert (d): the REPORT body verbatim — the same expectation part 1d has for the envelope's BodyText
        traceExit.Should().Be(0, trace.Stderr);
        string[] traceLines = trace.Stdout.Split('\n');
        traceLines[0].Should().StartWith($"job     {id}  ");
        traceLines[1].Should().Be("state   reported by home-laptop");
        traceLines[2].Should().StartWith($"report  {report.Header.Id.Value}  done  ").And.Contain("cost_usd ");
        traceLines[3].Should().Be("body:");
        trace.Stdout.Should().EndWith("body:\n" + ExpectedReportSection(await ScenarioResultTextAsync("done", ct)) + "\n");

        // Act (e): the audit
        var (verifyExit, verify) = await CliAsync(["verify"], _harness.NodeJsonPath, healthUrl: null, ct);
        await host.StopAsync(ct);

        // Assert (e)
        verifyExit.Should().Be(0, verify.Stdout + verify.Stderr);
        verify.Stdout.TrimEnd('\n').Split('\n')[^1].Should().MatchRegex(@"^verified \d+ ok, 0 failed, \d+ skipped; 1 tenant directory; HEAD [0-9a-f]{12}$");

        // Assert: no secret anywhere
        SecretSweep.AssertAbsent([submit.Stdout, submit.Stderr, status.Stdout, status.Stderr, trace.Stdout, trace.Stderr, verify.Stdout, verify.Stderr]);
        SecretSweep.AssertAbsent(_harness.LogLines);
        SecretSweep.AssertAbsent(_harness.Hooks.Calls.SelectMany(SecretSweep.TextsOf));
    }

    [Fact]
    public async Task Part1b_StatusWhileTheHostIsStopped_DownButBusStillListed()
    {
        // Arrange: one round trip through the host, then the host stopped — its port now answers nobody (spec 08 Failure modes "Status: down")
        var ct = TestContext.Current.CancellationToken;
        ScratchProject project = await _harness.ProjectAsync(ct);
        await _harness.SeedJobAsync(project, ct);
        using IHost host = _harness.Build();
        await host.StartAsync(ct);
        HealthServer server = host.Services.GetRequiredService<HealthServer>();
        await NodeHostHarness.WaitUntilAsync(async () => server.IsListening && (await _harness.ReportsOnMainAsync(ct)).Count == 1, TimeSpan.FromSeconds(60), ct);
        string healthUrl = $"http://127.0.0.1:{server.BoundPort}/health";
        await host.StopAsync(ct);

        // Act
        var (exit, console) = await CliAsync(["status"], _harness.NodeJsonPath, healthUrl, ct);

        // Assert: the owner's read-out when the service is not running — down, exit 6, the bus block complete
        exit.Should().Be(6, console.Stdout + console.Stderr);
        string[] lines = console.Stdout.Split('\n');
        lines[0].Should().Be($"node    home-laptop (acme) · health: down — no node service answering on {healthUrl}");
        lines[1].Should().MatchRegex(@"^bus     head [0-9a-f]{12} · 1 tenant directory$");
        lines[4].Should().MatchRegex(@"^        home-laptop +0 +0 +0 +1 +\d+$");
        lines[5].Should().Be("recent reports (1 of 1)");
        SecretSweep.AssertAbsent([console.Stdout, console.Stderr]);
    }

    #endregion

    #region Part 2 — 13

    [Fact]
    public async Task Part2_ContextFromHomeLaptop_PickedUpByCentral_GetContextReturnsIt_DigestUnderSixKTokens()
    {
        // Arrange: Central's production host over the node clone with its pickup over a realistic memory tree (plan 13 A13)
        var ct = TestContext.Current.CancellationToken;
        const string Fact = "The staging database is restored from Sunday's snapshot";
        var (memoryRoot, memoryEnv) = PickupTree();
        _harness.WriteNodeJson(machine: NodeHostHarness.Central);
        _harness.Environment = memoryEnv;
        var fingerprint = TreeFingerprint.Of(memoryRoot).Where(f => !f.Path.Contains("/inbox/", StringComparison.Ordinal)).ToList();
        using IHost host = _harness.Build();
        await host.StartAsync(ct);
        HealthServer server = host.Services.GetRequiredService<HealthServer>();
        await NodeHostHarness.WaitUntilAsync(() => Task.FromResult(server.IsListening), TimeSpan.FromSeconds(10), ct);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.BoundPort}/") };
        await NodeHostHarness.WaitUntilAsync(async () => (await HealthAsync(client, ct)).GetProperty("lastPollUtc").ValueKind == JsonValueKind.String, TimeSpan.FromSeconds(30), ct);

        // Act (a): the fact, from home-laptop
        var laptopEnv = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ZYGGY_NODE_JSON"] = HomeLaptopNodeJsonOverSender(),
            ["ZYGGY_SECRET_PATTERNS"] = Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt"),
        };
        var (contextExit, context) = await ZyggyCliInProcess.RunAsync(["context", "--scope", "project:calizr", "--", Fact], laptopEnv, CliServices, stdin: null, ct);

        // Assert (a)
        contextExit.Should().Be(0, context.Stderr);
        Match sent = ContextSentLine().Match(context.Stdout);
        sent.Success.Should().BeTrue(context.Stdout);
        var id = EnvelopeId.Parse(sent.Groups["id"].Value);
        string storedLine = context.Stdout.Split('\n')[1];
        storedLine.Should().EndWith(": " + Fact);

        // Act (b): Central's next pull picks it up
        string archived = BusPaths.EnvelopeFile(BusPaths.Archive(NodeHostHarness.Acme, id.Timestamp.UtcDateTime.Year, id.Timestamp.UtcDateTime.Month), id);
        string inbox = Path.Combine(memoryRoot, "acme", "alice", "inbox");
        await NodeHostHarness.WaitUntilAsync(
            async () => Directory.Exists(inbox)
                && Directory.EnumerateFiles(inbox, $"bus-context-home-laptop-{id.Value}-*.md").Any()
                && await _harness.Bus.ExistsOnMainAsync(archived, ct),
            TimeSpan.FromSeconds(30),
            ct);
        await host.StopAsync(ct);

        // Act (c): the Hub, the digest and the audit
        string text;
        await using (InProcessHub hub = await InProcessHub.StartAsync(new Zyggy.Core.Memory.MemoryPaths(memoryRoot, PickupMemory.Alice), TimeZoneInfo.Utc))
        {
            text = await hub.CallGetContextAsync("staging database");
        }

        var digests = new List<string>();
        foreach (string section in new[] { "identity", "index", "daily" })
        {
            // the digest is a raw verb writing straight to the process's stdout: the real binary, as the SessionStart hook runs it
            ZyggyRun digest = await ZyggyCli.RunAsync(["memory", "digest", section], memoryEnv, null, _harness.Scratch.Path, ct);
            digest.ExitCode.Should().Be(0, digest.Stderr);
            digests.Add(digest.Stdout);
        }

        var (verifyExit, verify) = await CliAsync(["verify"], _harness.NodeJsonPath, healthUrl: null, ct);

        // Assert (c)
        text.Should().Contain("## inbox (picked up, not yet filed by the dream)\n" + storedLine + "\n");
        int digestBytes = digests.Sum(d => Encoding.UTF8.GetByteCount(d));
        digestBytes.Should().BeGreaterThan(0).And.BeLessThanOrEqualTo(20_000, "the injected session memory stays under 6k tokens");
        digests.Should().AllSatisfy(d => d.Should().NotContain(Fact));
        (await _harness.Bus.ExistsOnMainAsync(BusPaths.EnvelopeFile(BusPaths.Context(NodeHostHarness.Acme, NodeHostHarness.HomeLaptop), id), ct)).Should().BeFalse();
        verifyExit.Should().Be(0, verify.Stdout + verify.Stderr);
        verify.Stdout.TrimEnd('\n').Split('\n')[^1].Should().MatchRegex(@"^verified \d+ ok, 0 failed, \d+ skipped; 1 tenant directory; HEAD [0-9a-f]{12}$");
        TreeFingerprint.Of(memoryRoot).Where(f => !f.Path.Contains("/inbox/", StringComparison.Ordinal)).Should().Equal(fingerprint);
        SecretSweep.AssertAbsent([context.Stdout, context.Stderr, text, verify.Stdout, verify.Stderr, .. digests]);
        SecretSweep.AssertAbsent(_harness.LogLines);
        SecretSweep.AssertAbsent(_harness.Hooks.Calls.SelectMany(SecretSweep.TextsOf));
        _harness.Hooks.Calls.Should().NotContain(c => c.WorkingDirectory.StartsWith(memoryRoot, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A <c>node.json</c> for <c>home-laptop</c> over the sender clone, signing with <c>acme/1</c> from the same file secret store.</summary>
    private string HomeLaptopNodeJsonOverSender() =>
        NodeJson.Write(
            _harness.Scratch.File("home-laptop-sender-config"),
            NodeHostHarness.Acme,
            NodeHostHarness.HomeLaptop,
            TestKeys.Acme1,
            busLocalPath: _harness.Bus.SenderDir,
            remote: TestHost.Remote.HttpsUrl!.ToString(),
            secretsRoot: _harness.SecretsRoot,
            nodeDir: _harness.Scratch.File("node-home-laptop-sender"));

    /// <summary>A scratch copy of the golden <c>digest/sided</c> tree (identity files, two sides, a ledger, a remember inbox file) and Central's pickup variables over it.</summary>
    private (string Root, Dictionary<string, string?> Env) PickupTree()
    {
        string root = _harness.Scratch.File("memory");
        string source = Path.Combine(Golden.Directory, "digest", "sided");
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(root, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        return (root, new Dictionary<string, string?>
        {
            ["ZYGGY_MEMORY_ROOT"] = root,
            ["ZYGGY_TENANT"] = "acme",
            ["ZYGGY_USER"] = "alice",
            ["ZYGGY_TIMEZONE"] = "UTC",
            ["ZYGGY_SECRET_PATTERNS"] = Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt"),
        });
    }

    [GeneratedRegex(@"^context (?<id>[0-9A-HJKMNP-TV-Z]{26}) to central \(")]
    private static partial Regex ContextSentLine();

    #endregion

    // ----- helpers shared by part 1b and later parts -----

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

    /// <summary>Runs a verb in process over <paramref name="nodeJson"/> with the harness's edges (the doorbell from the bare repository, recorded processes).</summary>
    private Task<(int Exit, VerbConsole Console)> CliAsync(string[] args, string nodeJson, string? healthUrl, CancellationToken ct)
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["ZYGGY_NODE_JSON"] = nodeJson };
        if (healthUrl is not null)
        {
            environment["ZYGGY_HEALTH_URL"] = healthUrl;
        }

        return ZyggyCliInProcess.RunAsync(args, environment, CliServices, stdin: null, ct);
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

    [GeneratedRegex(@"^submitted (?<id>[0-9A-HJKMNP-TV-Z]{26}) to home-laptop \(tenants/acme/nodes/home-laptop/jobs/\k<id>\.md\)\n\z")]
    private static partial Regex SubmittedLine();

    // ----- helpers shared by every part -----

    /// <summary>Runs the production host in process until <paramref name="count"/> reports are on <c>main</c>, then stops it (exit 0 asserted).</summary>
    private async Task RunHostUntilReportsAsync(int count, CancellationToken ct)
    {
        int exitCode = await _harness.RunUntilAsync(async () => (await _harness.ReportsOnMainAsync(ct)).Count == count, TimeSpan.FromSeconds(60), ct);
        exitCode.Should().Be(0, string.Join(Environment.NewLine, _harness.LogLines));
    }

    private async Task<Model> SingleReportAsync(CancellationToken ct) =>
        await _harness.ReportAsync((await _harness.ReportsOnMainAsync(ct)).Single(), ct);

    private static byte[] FlipSignatureByte(byte[] bytes)
    {
        string text = Encoding.UTF8.GetString(bytes);
        int at = text.IndexOf("sig: hmac-sha256:", StringComparison.Ordinal) + "sig: hmac-sha256:".Length;
        char hex = text[at];
        text = text[..at] + (hex == '0' ? '1' : '0') + text[(at + 1)..];
        return Encoding.UTF8.GetBytes(text);
    }

    private static async Task<JsonElement> HealthAsync(HttpClient client, CancellationToken ct)
    {
        using JsonDocument document = JsonDocument.Parse(await client.GetStringAsync(new Uri("/health", UriKind.Relative), ct));
        return document.RootElement.Clone();
    }

    // The REPORT section the way the spec defines it: from the first heading line REPORT (any Markdown level), else the whole text.
    private static string ExpectedReportSection(string resultText)
    {
        string text = resultText.Replace("\r", string.Empty, StringComparison.Ordinal);
        Match heading = ReportHeading().Match(text);
        return heading.Success ? text[heading.Index..] : text;
    }

    private static async Task<string> ScenarioResultTextAsync(string scenario, CancellationToken ct)
    {
        string[] lines = (await File.ReadAllTextAsync(Path.Combine(FakeClaude.ScenarioDirectory, scenario + ".jsonl"), ct))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using JsonDocument last = JsonDocument.Parse(lines[^1]);
        return last.RootElement.GetProperty("result").GetString()!;
    }

    [GeneratedRegex(@"^(#+[ \t]*)?REPORT[ \t]*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ReportHeading();
}
