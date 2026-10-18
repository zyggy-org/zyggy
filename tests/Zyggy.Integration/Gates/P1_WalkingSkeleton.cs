// The P1 walking-skeleton gate (founding spec §12), automated half. Parts and owners:
//   Part 1  (deliverable 07, this region): submit → real zyggy-node host (production DI, tenant acme) → fake claude → signed report on main,
//           opened by the ex-05 probe round trip, the tampered-job rejection and the foreign-tenant isolation.
//   Part 1b (deliverable 08): `zyggy status` over the same host and bus — append a `#region Part 1b — 08` below.
//   Part 2  (deliverable 13): the dream pass ingesting the bus — append a `#region Part 2 — 13` below.
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
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;
using Zyggy.Integration.Node;
using Zyggy.Node;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Gates;

/// <summary>Phase P1 gate, part 1 (spec 07 AC-23): the walking skeleton's automated half through the production host.</summary>
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
