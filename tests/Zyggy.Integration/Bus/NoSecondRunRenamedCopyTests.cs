using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Cli;
using Zyggy.Integration.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Bus;

/// <summary>
/// Spec 61 slice 2 correction 2 (AC-4): a byte-identical copy of a validly signed job placed in <c>jobs/</c> under another file name is
/// refused at intake (the signed <c>id</c> differs from the file name): moved to <c>jobs/rejected/</c> with a signed <c>rejected</c> report,
/// reason <c>invalid_field</c>, never claimed or run. The home laptop's listener with the production job runner and
/// <c>tools/fake-claude</c>, one bare repository, real git. The legitimate re-queue (same file name) is <see cref="NoSecondRunTests"/>.
/// </summary>
public sealed class NoSecondRunRenamedCopyTests : IAsyncLifetime
{
    private readonly HookedProcessRunner _hooks = TestHost.Hooks();

    private CliHarness _harness = null!;

    private ScratchProject _project = null!;

    private ServiceProvider _node = null!;

    public async ValueTask InitializeAsync()
    {
        _harness = await CliHarness.CreateAsync(CancellationToken.None);
        _project = await ScratchProject.CreateAsync(_harness.Bus, CancellationToken.None);
        _node = TestHost.BuildRunnerListener(
            _harness.Bus,
            CliHarness.HomeLaptop,
            await TestHost.StoreAsync(CliHarness.Acme, CancellationToken.None),
            FakeClaude.ExecutablePath,
            TestHost.NodeConfigurationFor(_harness.Bus, CliHarness.HomeLaptop),
            o =>
            {
                o.NodeDir = _harness.NodeDirOf("home-laptop-listener");
                o.TimeoutOverride = TimeSpan.FromSeconds(30);
                o.ModelEnvironment = new Dictionary<string, string> { ["ZYGGY_FAKE_CLAUDE_SCENARIO"] = "done" };
            },
            hooks: _hooks);
    }

    public async ValueTask DisposeAsync()
    {
        await _node.DisposeAsync();
        _project?.Dispose();
        await _harness.DisposeAsync();
    }

    // AC-4: the job ran and reported done; the same bytes come back under a new file name: refused, not run.
    [Fact]
    public async Task Intake_CopyOfDoneJobUnderNewName_RefusedNotRun()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (Model job, byte[] bytes) = await SeedJobAsync(ct);
        BusListener listener = _node.GetRequiredService<BusListener>();
        (await listener.StartAsync(ct)).Should().Be(BusStartupResult.Clean);
        EnvelopeId copy = await SeedCopyAsync(bytes, ct);
        (await _node.GetRequiredService<BusRepository>().PullAsync(ct)).Succeeded.Should().BeTrue();

        // Act
        await listener.ProcessOnceAsync(ct);

        // Assert
        await AssertCopyRefusedAsync(job, copy, ct);
    }

    // AC-4: the same bytes arrive under a new file name while the job's run is alive: refused, the model is started once.
    [Fact]
    public async Task Intake_CopyOfRunningJobUnderNewName_RefusedRunOnce()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (Model job, byte[] bytes) = await SeedJobAsync(ct);
        BusListener listener = _node.GetRequiredService<BusListener>();
        BusRepository repository = _node.GetRequiredService<BusRepository>();
        EnvelopeId? copy = null;
        _hooks.BeforeRun = async spec =>
        {
            if (copy is null && spec.FileName == FakeClaude.ExecutablePath)
            {
                copy = await SeedCopyAsync(bytes, ct);
                (await repository.PullAsync(ct)).Succeeded.Should().BeTrue();
                await listener.ProcessOnceAsync(ct);
            }
        };

        // Act
        (await listener.StartAsync(ct)).Should().Be(BusStartupResult.Clean);

        // Assert
        copy.Should().NotBeNull();
        await AssertCopyRefusedAsync(job, copy!, ct);
    }

    private static string QueuedPath(EnvelopeId id) => BusPaths.EnvelopeFile(BusPaths.Jobs(CliHarness.Acme, CliHarness.HomeLaptop), id);

    private static string ClaimedPath(EnvelopeId id) => BusPaths.EnvelopeFile(BusPaths.Claimed(CliHarness.Acme, CliHarness.HomeLaptop), id);

    private static string RejectedPath(EnvelopeId id) => BusPaths.EnvelopeFile(BusPaths.Rejected(CliHarness.Acme, CliHarness.HomeLaptop), id);

    // The job's done report and one rejected report (invalid_field) answering the copy's file id; the copy in jobs/rejected/, nothing left
    // in jobs/ or jobs/claimed/ under either id; the model started once.
    private async Task AssertCopyRefusedAsync(Model job, EnvelopeId copy, CancellationToken ct)
    {
        string output = await _harness.Bus.RunGitAsync(_harness.Bus.BareDir, ["ls-tree", "--name-only", "main", BusPaths.Reports(CliHarness.Acme, CliHarness.HomeLaptop) + "/"], ct);
        var reports = new List<Model>();
        foreach (string path in output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(p => p.EndsWith(".md", StringComparison.Ordinal)))
        {
            reports.Add(await _harness.ParseOnMainAsync(path, ct));
        }

        reports.Select(r => (r.Header.InReplyTo, r.Report!.Status, r.Report.Reason)).Should().BeEquivalentTo(
            [(job.Header.Id, ReportStatus.Done, (string?)null), (copy, ReportStatus.Rejected, "invalid_field")]);
        (await _harness.Bus.ExistsOnMainAsync(RejectedPath(copy), ct)).Should().BeTrue();
        (await _harness.Bus.ExistsOnMainAsync(QueuedPath(copy), ct)).Should().BeFalse();
        (await _harness.Bus.ExistsOnMainAsync(ClaimedPath(copy), ct)).Should().BeFalse();
        (await _harness.Bus.ExistsOnMainAsync(QueuedPath(job.Header.Id), ct)).Should().BeFalse();
        (await _harness.Bus.ExistsOnMainAsync(ClaimedPath(job.Header.Id), ct)).Should().BeFalse();
        _hooks.Calls.Count(c => c.FileName == FakeClaude.ExecutablePath).Should().Be(1, "the copy is never run");
    }

    private async Task<(Model Job, byte[] Bytes)> SeedJobAsync(CancellationToken ct)
    {
        Model job = await TestEnvelopes.SignedJobAsync(
            CliHarness.Central,
            CliHarness.HomeLaptop,
            _harness.Store,
            TestKeys.Acme1,
            "Read README.md and finish with a REPORT section; change nothing.\n",
            ct,
            fields: new JobFields(_project.Path, TimeoutMinutes: 2, AllowedTools: ["Read"], Attempt: 1));
        byte[] bytes = EnvelopeWriter.Serialize(job);
        await _harness.Bus.SeedRawFileAsync(QueuedPath(job.Header.Id), bytes, "acme job " + job.Header.Id.Value + " central→home-laptop", ct);
        return (job, bytes);
    }

    private async Task<EnvelopeId> SeedCopyAsync(byte[] bytes, CancellationToken ct)
    {
        var copy = EnvelopeId.New(DateTimeOffset.UtcNow);
        await _harness.Bus.SeedRawFileAsync(QueuedPath(copy), bytes, "acme job " + copy.Value + " central→home-laptop", ct);
        return copy;
    }
}
