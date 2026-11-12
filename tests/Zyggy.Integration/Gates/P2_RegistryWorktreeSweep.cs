// The P2 gate (founding spec §12), automated half. Parts and owners:
//   Part 1 (deliverable 14, this region): registry published, a job routed by name — `zyggy submit --project <name>` from central →
//           the real zyggy-node host resolves the name from its own scan → fake claude in the discovered folder → signed report on main.
//   Part 2 (deliverable 16, worktrees) and Part 3 (deliverable 17, sweep) append their own regions.
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
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;
using Zyggy.Integration.Node;
using Zyggy.Node;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Gates;

/// <summary>Phase P2 gate, part 1 (spec 14 AC-19, AC-24, AC-25): the registry on the bus and a job routed by registry name, through the production host and the CLI.</summary>
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
    private Task<(int Exit, VerbConsole Console)> CliAsync(string[] args, CancellationToken ct, string? nodeJson = null)
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["ZYGGY_NODE_JSON"] = nodeJson ?? CentralNodeJson() };
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

    private async Task<Model> SingleReportAsync(CancellationToken ct) =>
        await _harness.ReportAsync((await _harness.ReportsOnMainAsync(ct)).Single(), ct);

    private void SweepSecrets(params VerbConsole[] consoles)
    {
        SecretSweep.AssertAbsent(consoles.SelectMany(c => new[] { c.Stdout, c.Stderr }));
        SecretSweep.AssertAbsent(_harness.LogLines);
        SecretSweep.AssertAbsent(_harness.Hooks.Calls.SelectMany(SecretSweep.TextsOf));
    }

    [GeneratedRegex(@"^submitted (?<id>[0-9A-HJKMNP-TV-Z]{26}) to home-laptop \(tenants/acme/nodes/home-laptop/jobs/\k<id>\.md\)\n\z")]
    private static partial Regex SubmittedLine();
}
