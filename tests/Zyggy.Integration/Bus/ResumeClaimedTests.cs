using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Jobs;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Bus;

/// <summary>
/// Spec 07 AC-10 on the wire (founding spec §5 as amended by A7-5): a claim left behind by a previous run is run again by the real
/// listener with the job runner and the fake <c>claude</c> — report on <c>main</c>, no second claim commit.
/// </summary>
public sealed class ResumeClaimedTests : IAsyncLifetime
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName Central = MachineName.Parse("central");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private readonly BusRepoFixture _bus = new(Acme);

    private readonly ScratchDirectory _scratch = new();

    private readonly RecordingLoggerProvider _log = new();

    private InMemorySecretStore _store = null!;

    private ScratchProject _project = null!;

    private string NodeDir => Path.Combine(_scratch.Path, "node");

    public async ValueTask InitializeAsync()
    {
        await _bus.InitializeAsync();
        await _bus.SeedTenantAsync(Acme, [Central, HomeLaptop], CancellationToken.None);
        _store = await TestHost.StoreAsync(Acme, CancellationToken.None);
        _project = await ScratchProject.CreateAsync(_bus, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        _project?.Dispose();
        _scratch.Dispose();
        await _bus.DisposeAsync();
    }

    [Fact]
    public async Task ResumeClaimedAsync_ClaimedFileFromPreviousRun_ReportOnMainClaimedGoneNoClaimCommit()
    {
        // Arrange: the previous run's claim sits on main; this run starts fresh
        var ct = TestContext.Current.CancellationToken;
        Model job = await TestEnvelopes.SignedJobAsync(Central, HomeLaptop, _store, TestKeys.Acme1, "Read README.md and finish with a REPORT section; change nothing.\n", ct, fields: new JobFields(_project.Path, TimeoutMinutes: 2, AllowedTools: ["Read"]));
        string claimedPath = BusPaths.EnvelopeFile(BusPaths.Claimed(Acme, HomeLaptop), job.Header.Id);
        await _bus.SeedRawFileAsync(claimedPath, EnvelopeWriter.Serialize(job), "seed: a claim left behind by a previous run", ct);
        await using ServiceProvider services = Services();
        BusListener listener = services.GetRequiredService<BusListener>();
        (await listener.StartAsync(ct)).Should().Be(BusStartupResult.Clean);

        // Act
        int resumed = await listener.ResumeClaimedAsync(ct);
        IReadOnlyList<string> reports = await WaitForReportsAsync(1, ct);
        await listener.DrainAsync(ct);

        // Assert
        resumed.Should().Be(1);
        EnvelopeResult parsed = await new EnvelopeSigner(_store).ParseAndVerifyAsync(await _bus.ShowBytesAsync(reports.Single(), ct), Acme, ct);
        parsed.IsAccepted.Should().BeTrue(parsed.Rejection?.ToString());
        parsed.Envelope!.Header.InReplyTo.Should().Be(job.Header.Id);
        parsed.Envelope.Report!.Status.Should().Be(ReportStatus.Done);
        (await _bus.ExistsOnMainAsync(claimedPath, ct)).Should().BeFalse();
        string[] subjects = (await _bus.RunGitAsync(_bus.BareDir, ["log", "--format=%s", "main"], ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        subjects.Should().NotContain(s => s.StartsWith("acme claim ", StringComparison.Ordinal), "a resumed job is never claimed a second time");
        subjects.Should().ContainSingle(s => s.StartsWith("acme report ", StringComparison.Ordinal));
        File.Exists(TranscriptPaths.For(NodeDir, Acme, job.Header.Id)).Should().BeTrue();
        _log.Messages.Should().Contain("resuming 1 claimed job(s) from a previous run");
    }

    [Fact]
    public async Task ResumeClaimedAsync_TamperedClaimedFile_LeftOnMainWithWarning()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var id = EnvelopeId.New(DateTimeOffset.UtcNow);
        string claimedPath = BusPaths.EnvelopeFile(BusPaths.Claimed(Acme, HomeLaptop), id);
        byte[] tampered = await TestEnvelopes.SignedJobBytesAsync(Central, HomeLaptop, _store, TestKeys.Acme1, "probe\n", ct, b => { b[^2] ^= 0x01; return b; }, id);
        await _bus.SeedRawFileAsync(claimedPath, tampered, "seed: a tampered claim", ct);
        await using ServiceProvider services = Services();
        BusListener listener = services.GetRequiredService<BusListener>();
        (await listener.StartAsync(ct)).Should().Be(BusStartupResult.Clean);

        // Act
        int resumed = await listener.ResumeClaimedAsync(ct);
        await listener.DrainAsync(ct);

        // Assert
        resumed.Should().Be(0);
        (await _bus.ExistsOnMainAsync(claimedPath, ct)).Should().BeTrue();
        _log.AtLevel(LogLevel.Warning).Select(e => e.Message).Should().ContainSingle().Which.Should().Be($"claimed job {id.Value} does not verify; left for the sweep");
        (await WaitForReportsAsync(0, ct)).Should().BeEmpty();
    }

    private ServiceProvider Services() =>
        TestHost.BuildRunnerListener(
            _bus,
            HomeLaptop,
            _store,
            FakeClaude.ExecutablePath,
            TestHost.NodeConfigurationFor(_bus, HomeLaptop),
            o =>
            {
                o.NodeDir = NodeDir;
                o.TimeoutOverride = TimeSpan.FromSeconds(30);
                o.ModelEnvironment = new Dictionary<string, string> { ["ZYGGY_FAKE_CLAUDE_SCENARIO"] = "done" };
            },
            logging: _log,
            withDispatcher: true);

    private async Task<IReadOnlyList<string>> WaitForReportsAsync(int count, CancellationToken ct)
    {
        List<string> reports = [];
        for (int i = 0; i < 120; i++)
        {
            string output = await _bus.RunGitAsync(_bus.BareDir, ["ls-tree", "--name-only", "main", BusPaths.Reports(Acme, HomeLaptop) + "/"], ct);
            reports = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(p => p.EndsWith(".md", StringComparison.Ordinal)).ToList();
            if (reports.Count >= count)
            {
                break;
            }

            await Task.Delay(250, ct);
        }

        return reports;
    }
}
