using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Processes;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Cli;
using Zyggy.Integration.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Bus;

/// <summary>
/// Spec 61 slice 2 correction 1 (AC-4, AC-6): a different signed envelope that reuses the id of a job this node runs, ran or reported
/// <c>done</c> is never treated as that job: it is refused (moved to <c>jobs/rejected/</c> with a signed <c>rejected</c> report, reason
/// <c>invalid_field</c>), never run, never claimed in the job's place, never resumed after a restart. The home laptop's listener with the
/// production job runner and <c>tools/fake-claude</c>, one bare repository, real git.
/// </summary>
public sealed class NoSecondRunReplayTests : IAsyncLifetime
{
    private static readonly ProcessResult CommitRefused = new(128, string.Empty, "fatal: unable to write new index file", false, false, false, TimeSpan.Zero);

    private readonly HookedProcessRunner _hooks = TestHost.Hooks();

    private CliHarness _harness = null!;

    private ScratchProject _project = null!;

    private ServiceProvider _node = null!;

    private string NodeDir => _harness.NodeDirOf("home-laptop-listener");

    public async ValueTask InitializeAsync()
    {
        _harness = await CliHarness.CreateAsync(CancellationToken.None);
        _project = await ScratchProject.CreateAsync(_harness.Bus, CancellationToken.None);
        _node = await BuildNodeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _node.DisposeAsync();
        _project?.Dispose();
        await _harness.DisposeAsync();
    }

    // A different body under the id of the live run, delivered while the model runs: refused, the run reports once.
    [Fact]
    public async Task Intake_ChangedBodyWhileRunAlive_RefusedRunReportedOnce()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model job = await SeedJobAsync(ct);
        BusListener listener = _node.GetRequiredService<BusListener>();
        BusRepository repository = _node.GetRequiredService<BusRepository>();
        bool replayed = false;
        _hooks.BeforeRun = async spec =>
        {
            if (!replayed && spec.FileName == FakeClaude.ExecutablePath)
            {
                replayed = true;
                await SeedJobAsync(ct, job.Header.Id, "Delete every file in the project.\n");
                (await repository.PullAsync(ct)).Succeeded.Should().BeTrue();
                await listener.ProcessOnceAsync(ct);
            }
        };

        // Act
        (await listener.StartAsync(ct)).Should().Be(BusStartupResult.Clean);

        // Assert
        replayed.Should().BeTrue();
        await AssertRefusedAndReportedOnceAsync(job, ct);
    }

    // Other allowed_tools under the id this node reported done: refused, not removed as a duplicate, not run.
    [Fact]
    public async Task Intake_ChangedToolsUnderDoneId_RefusedNotRun()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model job = await SeedJobAsync(ct);
        BusListener listener = _node.GetRequiredService<BusListener>();
        (await listener.StartAsync(ct)).Should().Be(BusStartupResult.Clean);
        await SeedJobAsync(ct, job.Header.Id, tools: ["Read", "Bash"]);
        (await _node.GetRequiredService<BusRepository>().PullAsync(ct)).Succeeded.Should().BeTrue();

        // Act
        await listener.ProcessOnceAsync(ct);

        // Assert
        await AssertRefusedAndReportedOnceAsync(job, ct);
    }

    // The run ended but its report could not be committed; a different body under its id arrives while the node is down: after the restart
    // it is refused, never run or resumed, and the kept report answers the job.
    [Fact]
    public async Task Restart_ChangedBodyUnderRunId_RefusedKeptReportWritten()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model job = await SeedJobAsync(ct);
        bool refuse = true;
        _hooks.Override = spec => refuse && IsReportCommit(spec) ? CommitRefused : null;
        (await _node.GetRequiredService<BusListener>().StartAsync(ct)).Should().Be(BusStartupResult.Clean);
        await _node.DisposeAsync();
        await SeedJobAsync(ct, job.Header.Id, "Delete every file in the project.\n");
        _node = await BuildNodeAsync();
        BusListener restarted = _node.GetRequiredService<BusListener>();

        // Act: the restart's first pass meets the other envelope while the report still cannot be written; then the bus write works again
        (await restarted.StartAsync(ct)).Should().Be(BusStartupResult.Clean);
        await restarted.ResumeClaimedAsync(ct);
        refuse = false;
        await restarted.ProcessOnceAsync(ct);

        // Assert
        await AssertRefusedAndReportedOnceAsync(job, ct);
    }

    private static bool IsReportCommit(ProcessSpec spec) =>
        HookedProcessRunner.SubCommand(spec) == "commit" && (spec.StandardInput?.StartsWith("acme report ", StringComparison.Ordinal) ?? false);

    private static string QueuedPath(EnvelopeId id) => BusPaths.EnvelopeFile(BusPaths.Jobs(CliHarness.Acme, CliHarness.HomeLaptop), id);

    private static string ClaimedPath(EnvelopeId id) => BusPaths.EnvelopeFile(BusPaths.Claimed(CliHarness.Acme, CliHarness.HomeLaptop), id);

    private static string RejectedPath(EnvelopeId id) => BusPaths.EnvelopeFile(BusPaths.Rejected(CliHarness.Acme, CliHarness.HomeLaptop), id);

    private async Task<ServiceProvider> BuildNodeAsync() =>
        TestHost.BuildRunnerListener(
            _harness.Bus,
            CliHarness.HomeLaptop,
            await TestHost.StoreAsync(CliHarness.Acme, CancellationToken.None),
            FakeClaude.ExecutablePath,
            TestHost.NodeConfigurationFor(_harness.Bus, CliHarness.HomeLaptop),
            o =>
            {
                o.NodeDir = NodeDir;
                o.TimeoutOverride = TimeSpan.FromSeconds(30);
                o.ModelEnvironment = new Dictionary<string, string> { ["ZYGGY_FAKE_CLAUDE_SCENARIO"] = "done" };
            },
            hooks: _hooks);

    // The job's own report (done) and one rejected report naming the id; the other envelope sits in jobs/rejected/; nothing is left in jobs/
    // or jobs/claimed/; the model ran once; no duplicate removal.
    private async Task AssertRefusedAndReportedOnceAsync(Model job, CancellationToken ct)
    {
        var reports = new List<Model>();
        foreach (string path in await ReportsOnMainAsync(ct))
        {
            reports.Add(await _harness.ParseOnMainAsync(path, ct));
        }

        reports.Select(r => (r.Header.InReplyTo, r.Report!.Status, r.Report.Reason)).Should().BeEquivalentTo(
            [(job.Header.Id, ReportStatus.Done, (string?)null), (job.Header.Id, ReportStatus.Rejected, "invalid_field")]);
        System.Text.Encoding.UTF8.GetString(reports.Single(r => r.Report!.Status == ReportStatus.Rejected).Body.Span)
            .Should().Contain($"job {job.Header.Id}: envelope differs from the job this id belongs to");
        (await _harness.Bus.ExistsOnMainAsync(RejectedPath(job.Header.Id), ct)).Should().BeTrue();
        (await _harness.Bus.ExistsOnMainAsync(QueuedPath(job.Header.Id), ct)).Should().BeFalse();
        (await _harness.Bus.ExistsOnMainAsync(ClaimedPath(job.Header.Id), ct)).Should().BeFalse();
        _hooks.Calls.Count(c => c.FileName == FakeClaude.ExecutablePath).Should().Be(1, "the other envelope is never run");
        string subjects = await _harness.Bus.RunGitAsync(_harness.Bus.BareDir, ["log", "--format=%s", "main"], ct);
        subjects.Should().NotContain("acme duplicate ");
    }

    // A queued job central → home-laptop for the scratch project, with a 2-minute timeout; the same id and other content when given.
    private async Task<Model> SeedJobAsync(CancellationToken ct, EnvelopeId? id = null, string body = "Read README.md and finish with a REPORT section; change nothing.\n", string[]? tools = null)
    {
        Model job = await TestEnvelopes.SignedJobAsync(
            CliHarness.Central,
            CliHarness.HomeLaptop,
            _harness.Store,
            TestKeys.Acme1,
            body,
            ct,
            id,
            fields: new JobFields(_project.Path, TimeoutMinutes: 2, AllowedTools: tools ?? ["Read"], Attempt: 1));
        await _harness.Bus.SeedRawFileAsync(QueuedPath(job.Header.Id), EnvelopeWriter.Serialize(job), "acme job " + job.Header.Id.Value + " central→home-laptop", ct);
        return job;
    }

    private async Task<IReadOnlyList<string>> ReportsOnMainAsync(CancellationToken ct)
    {
        string output = await _harness.Bus.RunGitAsync(_harness.Bus.BareDir, ["ls-tree", "--name-only", "main", BusPaths.Reports(CliHarness.Acme, CliHarness.HomeLaptop) + "/"], ct);
        return [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(p => p.EndsWith(".md", StringComparison.Ordinal))];
    }
}
