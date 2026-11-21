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
/// Spec 59 slice 6 (AC-9): a job with <c>continue</c> starts its worktree at <c>origin/zyggy/&lt;id&gt;</c> of the earlier job; an id whose
/// branch is not on the origin is <c>rejected</c> / <c>branch_refused</c> before the model; <c>max_turns</c> reaches <c>tools/fake-claude</c>
/// capped by the node; and <c>zyggy submit --continue --max-turns</c> writes both fields. Production listener and job runner, real git.
/// </summary>
public sealed class ContinueJobTests : IAsyncLifetime
{
    private static readonly string[] AllowedTools = ["Read", "Edit"];

    private static readonly string[] OnlySecond = ["src/second.txt"];

    private CliHarness _harness = null!;

    private WorktreeProject _project = null!;

    private string CapturePath => _harness.Scratch.File("capture.bin");

    public async ValueTask InitializeAsync()
    {
        _harness = await CliHarness.CreateAsync(CancellationToken.None);
        _project = await WorktreeProject.CreateAsync(_harness.Bus, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        _project?.Dispose();
        await _harness.DisposeAsync();
    }

    // AC-9: the second job's branch starts at the first job's pushed tip and carries both jobs' files.
    [Fact]
    public async Task ProcessOnceAsync_ContinueEarlierJob_BranchStartsAtTheEarlierJobsTipOnOrigin()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        SeededJob earlier = await RunJobAsync(new JobFields(_project.Path, Worktree: true, TimeoutMinutes: 2, AllowedTools: AllowedTools), "+src/first.txt\tfirst", ct);
        string? earlierTip = await _project.ResolveAsync(_project.OriginDir, "refs/heads/zyggy/" + earlier.Id.Value, ct);

        // Act
        SeededJob job = await RunJobAsync(new JobFields(_project.Path, Worktree: true, TimeoutMinutes: 2, AllowedTools: AllowedTools, Continue: earlier.Id), "+src/second.txt\tsecond", ct);

        // Assert
        Model report = await ReportOfAsync(job.Id, ct);
        new
        {
            report.Report!.Status,
            report.Report.FilesChanged,
            Parent = await _project.ResolveAsync(_project.OriginDir, "refs/heads/zyggy/" + job.Id.Value + "^", ct),
            First = await _project.OriginGitAsync(["show", "refs/heads/zyggy/" + job.Id.Value + ":src/first.txt"], ct),
        }.Should().BeEquivalentTo(new { Status = ReportStatus.Done, FilesChanged = OnlySecond, Parent = earlierTip, First = "first" });
    }

    // Failure modes: the named job's branch is not on the origin.
    [Fact]
    public async Task ProcessOnceAsync_ContinueUnknownJob_RejectedBranchRefused_ModelNeverStarted()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        EnvelopeId unknown = EnvelopeId.New(DateTimeOffset.UtcNow.AddMinutes(-10));

        // Act
        SeededJob job = await RunJobAsync(new JobFields(_project.Path, Worktree: true, TimeoutMinutes: 2, AllowedTools: AllowedTools, Continue: unknown), "+src/x.txt\tx", ct);

        // Assert
        Model report = await ReportOfAsync(job.Id, ct);
        new { report.Report!.Status, report.Report.Reason, report.BodyText, Started = File.Exists(CapturePath) }
            .Should().BeEquivalentTo(new { Status = ReportStatus.Rejected, Reason = "branch_refused", BodyText = "branch_refused: continue: zyggy/" + unknown.Value + " not found\n", Started = false });
    }

    // AC-9: max_turns reaches the model's command line, capped by the node's claude.maxTurns (60 in the test node.json).
    [Theory]
    [InlineData(7, "7", false)]
    [InlineData(500, "60", true)]
    public async Task ProcessOnceAsync_MaxTurns_ReachesFakeClaudeCappedByTheNode(int maxTurns, string expected, bool lowered)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;

        // Act
        SeededJob job = await RunJobAsync(new JobFields(_project.Path, Worktree: true, TimeoutMinutes: 2, AllowedTools: AllowedTools, MaxTurns: maxTurns), "+src/x.txt\tx", ct);

        // Assert
        IReadOnlyList<string> arguments = FakeClaude.ReadCapture(CapturePath).Arguments;
        Model report = await ReportOfAsync(job.Id, ct);
        new { Turns = arguments[arguments.ToList().IndexOf("--max-turns") + 1], Lowered = report.BodyText.Contains("max_turns lowered to 60\n", StringComparison.Ordinal) }
            .Should().BeEquivalentTo(new { Turns = expected, Lowered = lowered });
    }

    // CLI: --continue and --max-turns on a model job.
    [Fact]
    public async Task Submit_ContinueAndMaxTurns_SignedJobCarriesBoth()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        EnvelopeId earlier = EnvelopeId.New(DateTimeOffset.UtcNow.AddMinutes(-10));

        // Act
        var (exit, console) = await _harness.RunAsync(
            ["submit", "--to", "home-laptop", "--project", "shop", "--worktree", "--continue", earlier.Value, "--max-turns", "120", "Go on.\n"], _harness.CentralNodeJson, ct: ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        var id = EnvelopeId.Parse(console.Stdout.Split(' ', StringSplitOptions.RemoveEmptyEntries).Single(w => EnvelopeId.TryParse(w.Trim(), out _)).Trim());
        Model job = await _harness.ParseOnMainAsync(BusPaths.EnvelopeFile(BusPaths.Jobs(CliHarness.Acme, CliHarness.HomeLaptop), id), ct);
        new { job.Job!.Continue, job.Job.MaxTurns }.Should().BeEquivalentTo(new { Continue = earlier, MaxTurns = 120 });
    }

    [Fact]
    public async Task Submit_ContinueWithoutWorktree_Usage()
    {
        // Act
        var ct = TestContext.Current.CancellationToken;
        var (exit, console) = await _harness.RunAsync(
            ["submit", "--to", "home-laptop", "--project", "shop", "--continue", EnvelopeId.New(DateTimeOffset.UtcNow).Value, "Go on.\n"], _harness.CentralNodeJson, ct: ct);

        // Assert
        new { exit, console.Stderr }.Should().BeEquivalentTo(new { exit = 2, Stderr = "zyggy: continue: --continue requires --worktree\n" });
    }

    private async Task<SeededJob> RunJobAsync(JobFields fields, string edits, CancellationToken ct)
    {
        File.Delete(CapturePath);
        var environment = new Dictionary<string, string>
        {
            ["ZYGGY_FAKE_CLAUDE_SCENARIO"] = "done",
            ["ZYGGY_FAKE_CLAUDE_CAPTURE"] = CapturePath,
            ["ZYGGY_FAKE_CLAUDE_EDITS"] = edits,
        };
        NodeConfiguration configuration = TestHost.NodeConfigurationFor(_harness.Bus, CliHarness.HomeLaptop);
        await using ServiceProvider services = TestHost.BuildRunnerListener(_harness.Bus, CliHarness.HomeLaptop, _harness.Store, FakeClaude.ExecutablePath, configuration, o =>
        {
            o.NodeDir = _project.NodeDir;
            o.TimeoutOverride = TimeSpan.FromSeconds(30);
            o.ModelEnvironment = environment;
        });
        (await services.GetRequiredService<BusListener>().StartAsync(ct)).Should().Be(BusStartupResult.Clean);
        SeededJob job = await _harness.Bus.SeedSignedJobAsync(CliHarness.Central, CliHarness.HomeLaptop, TestKeys.Acme1, _harness.Store, "Change the shop.\n", fields: fields, ct: ct);
        (await services.GetRequiredService<IBusProvider>().PullAsync(ct)).Succeeded.Should().BeTrue();
        (await services.GetRequiredService<BusListener>().ProcessOnceAsync(ct)).Should().Be(1);
        return job;
    }

    private async Task<Model> ReportOfAsync(EnvelopeId job, CancellationToken ct)
    {
        string reports = BusPaths.Reports(CliHarness.Acme, CliHarness.HomeLaptop);
        string listing = await _harness.Bus.RunGitAsync(_harness.Bus.BareDir, ["ls-tree", "-r", "--name-only", "main", "--", reports], ct);
        var parsed = new List<Model>();
        foreach (string path in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(p => p.EndsWith(".md", StringComparison.Ordinal)))
        {
            parsed.Add(await _harness.ParseOnMainAsync(path, ct));
        }

        return parsed.Should().ContainSingle(r => r.Header.InReplyTo == job).Subject;
    }
}
