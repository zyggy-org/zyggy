using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Envelope;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Cli;
using Zyggy.Integration.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Jobs;

/// <summary>
/// Spec 59 slice 1 (Wire): a signed <c>fetch</c> job through the production node composition, real <c>git</c> on a project whose
/// <c>origin</c> is a bare repository, and <c>tools/fake-claude</c> configured but never started. The report lands on the bus with the
/// file attached beside it (AC-1), <c>zyggy report --file</c> writes it back out (AC-3), and <c>zyggy submit --fetch</c> refuses a machine
/// that does not advertise the kind (AC-5).
/// </summary>
public sealed class FetchJobTests : IAsyncLifetime
{
    private const string Docs = "feature/docs";

    // CRLF, a non-ASCII character and no final newline: a byte-for-byte check, not a line check.
    private static readonly byte[] Plan = Encoding.UTF8.GetBytes("# Plan\r\nstep één\r\nlast line");

    private CliHarness _harness = null!;

    private WorktreeProject _project = null!;

    private string CapturePath => _harness.Scratch.File("capture.bin");

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        _harness = await CliHarness.CreateAsync(ct);
        _project = await WorktreeProject.CreateAsync(_harness.Bus, ct);
        await _project.GitAsync(["checkout", "-q", "-b", Docs], ct);
        Directory.CreateDirectory(Path.Combine(_project.Path, "docs"));
        await File.WriteAllBytesAsync(Path.Combine(_project.Path, "docs", "plan.md"), Plan, ct);
        await _project.GitAsync(["add", "-A"], ct);
        await _project.GitAsync(["commit", "-q", "-m", "plan"], ct);
        await _project.GitAsync(["push", "-q", "origin", Docs], ct);
        await _project.GitAsync(["checkout", "-q", "main"], ct);
    }

    public async ValueTask DisposeAsync()
    {
        _project?.Dispose();
        await _harness.DisposeAsync();
    }

    // AC-1: done, the model never started, the attachment on the bus equals the file at origin/<ref> and the signed report names it.
    [Fact]
    public async Task FetchJob_FileOnOriginBranch_ReportDoneAttachmentEqualsFileModelNeverStarted()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;

        // Act
        SeededJob job = await ProcessAsync("docs/plan.md", Docs, ct);

        // Assert
        (Model report, string path) = await ReportForAsync(job, ct);
        byte[] attached = await _harness.Bus.ShowBytesAsync(Path.ChangeExtension(path, ".att"), ct);
        new
        {
            report.Report!.Status,
            report.Report.FileName,
            report.Report.FileSha256,
            Attached = attached,
            ModelStarted = File.Exists(CapturePath),
        }.Should().BeEquivalentTo(new
        {
            Status = ReportStatus.Done,
            FileName = "plan.md",
            FileSha256 = Convert.ToHexStringLower(SHA256.HashData(Plan)),
            Attached = Plan,
            ModelStarted = false,
        });
    }

    // AC-2 (Wire): a file missing at that ref is failed/git_error with git's own message.
    [Fact]
    public async Task FetchJob_FileMissingAtRef_FailedGitErrorWithGitsMessage()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;

        // Act
        SeededJob job = await ProcessAsync("docs/plan.md", "main", ct);

        // Assert
        Model report = (await ReportForAsync(job, ct)).Report;
        new { report.Report!.Status, report.Report.Reason, report.BodyText }.Should().BeEquivalentTo(new
        {
            Status = ReportStatus.Failed,
            Reason = "git_error",
            BodyText = "git_error: fetch: fatal: path 'docs/plan.md' does not exist in 'origin/main'\n",
        });
    }

    // AC-1 / AC-3: `zyggy report <id> --file <out>` writes the attached file, byte for byte, after checking it against the signed sha.
    [Fact]
    public async Task FetchJob_ReportFileOption_WritesAttachedFileByteForByte()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        SeededJob job = await ProcessAsync("docs/plan.md", Docs, ct);
        string output = _harness.Scratch.File("out.md");

        // Act
        var (exit, console) = await _harness.RunAsync(["report", job.Id.Value, "--file", output], _harness.HomeLaptopNodeJson, ct: ct);

        // Assert
        new { Exit = exit, Bytes = await File.ReadAllBytesAsync(output, ct) }.Should().BeEquivalentTo(new { Exit = 0, Bytes = Plan }, console.Stderr);
    }

    // AC-5: the CLI refuses --fetch to a machine whose registry does not advertise fetch; nothing is written to the bus.
    [Fact]
    public async Task FetchJob_SubmitToMachineWithoutFetchCapability_RefusedNothingOnTheBus()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        int before = await _harness.CommitCountOnMainAsync(ct);

        // Act
        var (exit, console) = await _harness.RunAsync(
            ["submit", "--to", "home-laptop", "--project", "shop", "--fetch", "docs/plan.md", "--ref", Docs], _harness.CentralNodeJson, ct: ct);

        // Assert
        new { Exit = exit, Commits = await _harness.CommitCountOnMainAsync(ct), Refused = console.Stderr.Contains("kind", StringComparison.Ordinal) }
            .Should().BeEquivalentTo(new { Exit = 2, Commits = before, Refused = true }, console.Stderr);
    }

    // The host starts first (StartAsync's checks stay in the test, as in WorktreeRoundTripTests), then the job is seeded, pulled and run in one pass.
    private async Task<SeededJob> ProcessAsync(string path, string reference, CancellationToken ct)
    {
        NodeConfiguration configuration = TestHost.NodeConfigurationFor(_harness.Bus, CliHarness.HomeLaptop);
        await using ServiceProvider services = TestHost.BuildRunnerListener(_harness.Bus, CliHarness.HomeLaptop, _harness.Store, FakeClaude.ExecutablePath, configuration, o =>
        {
            o.NodeDir = _project.NodeDir;
            o.TimeoutOverride = TimeSpan.FromSeconds(30);
            o.ModelEnvironment = new Dictionary<string, string> { ["ZYGGY_FAKE_CLAUDE_SCENARIO"] = "done", ["ZYGGY_FAKE_CLAUDE_CAPTURE"] = CapturePath };
        });
        BusListener listener = services.GetRequiredService<BusListener>();
        (await listener.StartAsync(ct)).Should().Be(BusStartupResult.Clean);
        var fields = new JobFields(_project.Path, TimeoutMinutes: 2, Kind: JobKind.Fetch, FetchPath: path, FetchRef: reference);
        SeededJob job = await _harness.Bus.SeedSignedJobAsync(CliHarness.Central, CliHarness.HomeLaptop, TestKeys.Acme1, _harness.Store, "fetch " + path + "\n", fields: fields, ct: ct);
        (await services.GetRequiredService<IBusProvider>().PullAsync(ct)).Succeeded.Should().BeTrue();
        (await listener.ProcessOnceAsync(ct)).Should().Be(1);
        return job;
    }

    private async Task<(Model Report, string Path)> ReportForAsync(SeededJob job, CancellationToken ct)
    {
        string output = await _harness.Bus.RunGitAsync(_harness.Bus.BareDir, ["ls-tree", "--name-only", "main", BusPaths.Reports(CliHarness.Acme, CliHarness.HomeLaptop) + "/"], ct);
        var reports = new List<(Model, string)>();
        foreach (var path in output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(p => p.EndsWith(".md", StringComparison.Ordinal)))
        {
            Model report = await _harness.ParseOnMainAsync(path, ct);
            if (report.Header.InReplyTo == job.Id)
            {
                reports.Add((report, path));
            }
        }

        return reports.Should().ContainSingle().Subject;
    }
}
