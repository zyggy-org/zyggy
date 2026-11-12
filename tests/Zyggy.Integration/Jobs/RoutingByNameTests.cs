using System.Text;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Registry;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;
using Zyggy.Integration.Node;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Jobs;

/// <summary>
/// Plan 14 Step 10 (AC-17, AC-18, AC-26): through the production host on the bare repository with the fake <c>claude</c>, a job names a
/// project by registry name and runs in the folder the node discovered; an unknown name and a path outside <c>devRoots</c> are refused
/// before the model starts; the bus copy of the registry never steers a run.
/// </summary>
public sealed class RoutingByNameTests : IAsyncLifetime
{
    private const string Capture = "capture.bin";

    private NodeHostHarness _harness = null!;

    public async ValueTask InitializeAsync() => _harness = await NodeHostHarness.CreateAsync(CancellationToken.None);

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    [Fact]
    public async Task JobByName_RunsInDiscoveredFolder_DoneReport()
    {
        // Arrange: the job waits on the bus before the host starts (the A1 case)
        var ct = TestContext.Current.CancellationToken;
        ScratchProject calizr = await _harness.ProjectAsync(ct, "calizr");
        SeededJob job = await _harness.SeedJobAsync(calizr, ct, projectField: "calizr");

        // Act
        int exit = await _harness.RunUntilAsync(async () => (await _harness.ReportsOnMainAsync(ct)).Count == 1, TimeSpan.FromSeconds(60), ct);

        // Assert
        exit.Should().Be(0);
        Model report = await SingleReportAsync(ct);
        report.Header.InReplyTo.Should().Be(job.Id);
        report.Report!.Status.Should().Be(ReportStatus.Done, Encoding.UTF8.GetString(report.Body.Span));
        File.Exists(Path.Combine(calizr.Path, Capture)).Should().BeTrue("the fake claude ran in the discovered folder");
        SweepSecrets();
    }

    [Fact]
    public async Task JobByUnknownName_RejectedUnknownProject_ModelNeverStarted()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        ScratchProject calizr = await _harness.ProjectAsync(ct, "calizr");
        await _harness.SeedJobAsync(calizr, ct, projectField: "no-such-project");

        // Act
        int exit = await _harness.RunUntilAsync(async () => (await _harness.ReportsOnMainAsync(ct)).Count == 1, TimeSpan.FromSeconds(60), ct);

        // Assert
        exit.Should().Be(0);
        await AssertRejectedAsync("unknown_project: unknown name\n", ct);
        Directory.EnumerateFiles(_harness.ProjectsRoot, Capture, SearchOption.AllDirectories).Should().BeEmpty("the model never started");
        string transcripts = Path.Combine(_harness.NodeDir, "runs", "acme");
        (Directory.Exists(transcripts) ? Directory.EnumerateFileSystemEntries(transcripts) : []).Should().BeEmpty();
        SweepSecrets();
    }

    [Fact]
    public async Task JobByPathOutsideDevRoots_RejectedOutsideDevRoots()
    {
        // Arrange: a valid project, but under <temp>/zyggy-it rather than the host's devRoots entry (ProjectsRoot)
        var ct = TestContext.Current.CancellationToken;
        using ScratchProject outside = await ScratchProject.CreateAsync(_harness.Bus, ct);
        await _harness.SeedJobAsync(outside, ct);

        // Act
        int exit = await _harness.RunUntilAsync(async () => (await _harness.ReportsOnMainAsync(ct)).Count == 1, TimeSpan.FromSeconds(60), ct);

        // Assert
        exit.Should().Be(0);
        await AssertRejectedAsync("unknown_project: outside devRoots\n", ct);
        File.Exists(Path.Combine(outside.Path, Capture)).Should().BeFalse("the model never started");
        SweepSecrets();
    }

    [Fact]
    public async Task TamperedRegistryOnBus_NeverSteersRun_FileRestored()
    {
        // Arrange: AC-26 — once the node has published, someone rewrites calizr's path on the bus to a decoy project (also inside
        // devRoots, so only the source of the resolution can tell them apart) and a job by name follows
        var ct = TestContext.Current.CancellationToken;
        ScratchProject calizr = await _harness.ProjectAsync(ct, "calizr");
        ScratchProject decoy = await _harness.ProjectAsync(ct, "decoy");
        _harness.DiscoveryInterval = TimeSpan.FromSeconds(2);
        string realPath = calizr.Path.Replace('\\', '/');
        bool tampered = false;

        // Act
        int exit = await _harness.RunUntilAsync(
            async () =>
            {
                RegistryDocument? document = await _harness.RegistryOnMainAsync(NodeHostHarness.HomeLaptop, ct);
                if (document is null)
                {
                    return false;
                }

                if (!tampered)
                {
                    tampered = true;
                    RegistryDocument forged = document with
                    {
                        Projects = [.. document.Projects.Select(p => p.Name == "calizr" ? p with { Path = decoy.Path.Replace('\\', '/') } : p)],
                    };
                    await _harness.Bus.SeedRawFileAsync(BusPaths.Registry(NodeHostHarness.Acme, NodeHostHarness.HomeLaptop), forged.Serialize(), "tamper registry", ct);
                    await _harness.SeedJobAsync(calizr, ct, projectField: "calizr");
                    return false;
                }

                // Done once the job is reported and the node has put its own content back.
                return (await _harness.ReportsOnMainAsync(ct)).Count == 1
                    && document.Projects.Single(p => p.Name == "calizr").Path == realPath;
            },
            TimeSpan.FromSeconds(90),
            ct);

        // Assert
        exit.Should().Be(0);
        (await SingleReportAsync(ct)).Report!.Status.Should().Be(ReportStatus.Done);
        File.Exists(Path.Combine(calizr.Path, Capture)).Should().BeTrue("the node resolves from its own scan");
        File.Exists(Path.Combine(decoy.Path, Capture)).Should().BeFalse("the bus copy never steers a run");
        RegistryDocument restored = (await _harness.RegistryOnMainAsync(NodeHostHarness.HomeLaptop, ct))!;
        restored.Projects.Single(p => p.Name == "calizr").Path.Should().Be(realPath);
        SweepSecrets();
    }

    [Fact]
    public async Task TwoJobsSameNameAndPath_SecondWaitsForTheFirst()
    {
        // Arrange: AC-18 — two slots, one job by name and one by the same project's path; each run takes a few seconds
        var ct = TestContext.Current.CancellationToken;
        _harness.WriteNodeJson(maxConcurrentJobs: 2);
        _harness.FakeEnvironment["ZYGGY_FAKE_CLAUDE_DELAY_MS"] = "4000";
        ScratchProject calizr = await _harness.ProjectAsync(ct, "calizr");
        SeededJob byName = await _harness.SeedJobAsync(calizr, ct, projectField: "calizr");
        SeededJob byPath = await _harness.SeedJobAsync(calizr, ct);

        // Act
        int exit = await _harness.RunUntilAsync(async () => (await _harness.ReportsOnMainAsync(ct)).Count == 2, TimeSpan.FromSeconds(90), ct);

        // Assert: oldest first, the claims and reports alternate — the second claim comes only after the first report
        exit.Should().Be(0);
        string[] claimsAndReports = [.. (await _harness.CommitSubjectsAsync(ct)).Reverse()
            .Select(s => s.Split(' ')[1])
            .Where(action => action is "claim" or "report")];
        claimsAndReports.Should().Equal("claim", "report", "claim", "report");
        _harness.LogLines.Where(l => l.Contains("deferred: ProjectBusy", StringComparison.Ordinal)).Should().ContainSingle()
            .Which.Should().ContainAny(byName.Id.Value, byPath.Id.Value);
        foreach (string path in await _harness.ReportsOnMainAsync(ct))
        {
            (await _harness.ReportAsync(path, ct)).Report!.Status.Should().Be(ReportStatus.Done);
        }

        SweepSecrets();
    }

    private async Task<Model> SingleReportAsync(CancellationToken ct) =>
        await _harness.ReportAsync((await _harness.ReportsOnMainAsync(ct)).Single(), ct);

    private async Task AssertRejectedAsync(string body, CancellationToken ct)
    {
        Model report = await SingleReportAsync(ct);
        report.Report!.Status.Should().Be(ReportStatus.Rejected);
        report.Report.Reason.Should().Be("unknown_project");
        Encoding.UTF8.GetString(report.Body.Span).Should().Be(body);
    }

    private void SweepSecrets()
    {
        SecretSweep.AssertAbsent(_harness.LogLines);
        SecretSweep.AssertAbsent(_harness.Hooks.Calls.SelectMany(SecretSweep.TextsOf));
    }
}
