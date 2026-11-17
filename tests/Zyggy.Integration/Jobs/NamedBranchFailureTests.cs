using System.Net;
using System.Net.Sockets;

using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Envelope;
using Zyggy.Core.Processes;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Cli;
using Zyggy.Integration.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Jobs;

/// <summary>
/// Spec 53 slice 6 (Wire, failure path): hand-written signed jobs carrying <c>branch</c> through the production node composition root,
/// real <c>git</c> on a project whose <c>origin</c> is a second bare repository, and <c>tools/fake-claude</c>. A default-branch refusal
/// reaches the bus and <c>zyggy status</c>; a refused named push, a lost race, a failed and a timed-out run leave the named branch on the
/// origin unchanged with the work on <c>zyggy/&lt;id&gt;</c>; an owner's local branch of the job's name is never touched.
/// </summary>
public sealed class NamedBranchFailureTests : IAsyncLifetime
{
    private const string Cart = "feature/12-cart";

    private static readonly string[] AllowedTools = ["Read", "Edit"];

    private static readonly string[] ForbiddenGitWords = ["--force", "-f", "-B", "--force-with-lease", "reset", "update-ref", "merge", "rebase", "switch", "checkout"];

    private readonly HookedProcessRunner _nodeHooks = TestHost.Hooks();

    private CliHarness _harness = null!;

    private InMemorySecretStore _store = null!;

    private WorktreeProject _project = null!;

    private int _closedPort;

    private BusRepoFixture Bus => _harness.Bus;

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        _harness = await CliHarness.CreateAsync(ct);
        _store = await TestHost.StoreAsync(CliHarness.Acme, ct);
        _project = await WorktreeProject.CreateAsync(Bus, ct);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _closedPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
    }

    public async ValueTask DisposeAsync()
    {
        _project?.Dispose();
        await _harness.DisposeAsync();
    }

    // AC-8d: a default-branch refusal travels the runner's pre-model refusal path: a signed rejected report in reports/, the claimed file
    // gone, nothing in jobs/rejected/, and the origin's default branch untouched.
    [Fact]
    public async Task ProcessOnceAsync_BranchIsDefaultInOtherCase_RejectedReportClaimGoneRejectedFolderEmptyMainUnchanged()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var main = await _project.ResolveAsync(_project.OriginDir, "refs/heads/main", ct);
        await using var services = await StartAsync(Done("+src/cart.txt\tcart"), ct);
        var job = await SeedAndPullAsync(services, "Main", ct);

        // Act
        await ProcessAsync(services, ct);

        // Assert
        var report = await ReportForAsync(job, ct);
        new
        {
            report.Report!.Status,
            report.Report.Reason,
            report.BodyText,
            Claimed = await Bus.ExistsOnMainAsync(BusPaths.EnvelopeFile(BusPaths.Claimed(CliHarness.Acme, CliHarness.HomeLaptop), job.Id), ct),
            Queued = await Bus.ExistsOnMainAsync(job.RelativePath, ct),
            RejectedEnvelopes = (await Bus.RunGitAsync(Bus.BareDir, ["ls-tree", "-r", "--name-only", "main", BusPaths.Rejected(CliHarness.Acme, CliHarness.HomeLaptop) + "/"], ct)).Split('\n').Count(p => p.EndsWith(".md", StringComparison.Ordinal)),
            Main = await _project.ResolveAsync(_project.OriginDir, "refs/heads/main", ct),
            OriginBranches = await _project.OriginGitAsync(["for-each-ref", "--format=%(refname)", "refs/heads/"], ct),
        }.Should().BeEquivalentTo(new
        {
            Status = ReportStatus.Rejected,
            Reason = "branch_refused",
            BodyText = "branch_refused: default branch main\n",
            Claimed = false,
            Queued = false,
            RejectedEnvelopes = 0,
            Main = main,
            OriginBranches = "refs/heads/main",
        });
    }

    // AC-8e: whoever follows the job sees the node's refusal, not a git failure.
    [Fact]
    public async Task StatusId_AfterDefaultBranchRefusal_PrintsRejectedByHomeLaptopBranchRefused()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var services = await StartAsync(Done("+src/cart.txt\tcart"), ct);
        var job = await SeedAndPullAsync(services, "MAIN", ct);
        await ProcessAsync(services, ct);

        // Central's checkout catches up with the bus, as its own listener would.
        await Bus.RunGitAsync(Bus.SenderDir, ["pull", "-q", "--rebase", "origin", "main"], ct);

        // Act
        var (exit, console) = await _harness.RunAsync(["status", "--id", job.Id.Value], _harness.CentralNodeJson, extraEnvironment: ClosedHealth, ct: ct);

        // Assert
        new { Exit = exit, State = console.Stdout.Split('\n')[1] }
            .Should().BeEquivalentTo(new { Exit = 0, State = "state   rejected by home-laptop: branch_refused" }, console.Stderr);
    }

    // AC-7b: the origin refuses updates of the named branch only; the run ends failed / git_error, the named tip on the origin is unchanged,
    // and the job's commit reached the origin under zyggy/<id>.
    [Fact]
    public async Task ProcessOnceAsync_OriginRefusesNamedBranchOnly_FailedGitErrorNamedTipUnchangedWorkOnZyggyBranch()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await _project.GitAsync(["push", "-q", "origin", "main:refs/heads/" + Cart], ct);
        var named = await _project.ResolveAsync(_project.OriginDir, "refs/heads/" + Cart, ct);
        await RefuseNamedBranchOnOriginAsync(ct);
        await using var services = await StartAsync(Done("+src/cart.txt\tcart"), ct);
        var job = await SeedAndPullAsync(services, Cart, ct);

        // Act
        await ProcessAsync(services, ct);

        // Assert
        var report = await ReportForAsync(job, ct);
        new
        {
            report.Report!.Status,
            report.Report.Reason,
            NotChanged = report.BodyText.Contains("\nbranch " + Cart + " not changed: push refused\n", StringComparison.Ordinal),
            Named = await _project.ResolveAsync(_project.OriginDir, "refs/heads/" + Cart, ct),
            Work = await _project.OriginGitAsync(["show", "refs/heads/" + JobBranch(job) + ":src/cart.txt"], ct),
        }.Should().BeEquivalentTo(new { Status = ReportStatus.Failed, Reason = "git_error", NotChanged = true, Named = named, Work = "cart" });
    }

    // AC-7c: the owner's local branch of the job's name has diverged from origin's; the job starts at origin's tip, adds only its own
    // commit there, and the owner's local branch keeps its tip.
    [Fact]
    public async Task ProcessOnceAsync_OwnersLocalBranchDiverged_LocalTipUnchangedOriginGetsStartPlusJobCommitOnly()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var other = await OtherCloneAsync(ct);
        await CommitAsync(other, Cart, "colleague.txt", ct);
        await Bus.RunGitAsync(other, ["push", "-q", "origin", Cart], ct);
        var start = await _project.ResolveAsync(_project.OriginDir, "refs/heads/" + Cart, ct);
        var owner = await OwnerCommitOnLocalBranchAsync(ct);
        await using var services = await StartAsync(Done("+src/cart.txt\tcart"), ct);
        var job = await SeedAndPullAsync(services, Cart, ct);

        // Act
        await ProcessAsync(services, ct);

        // Assert
        new
        {
            (await ReportForAsync(job, ct)).Report!.Status,
            Local = await _project.ResolveAsync(_project.Path, "refs/heads/" + Cart, ct),
            Parent = await _project.ResolveAsync(_project.OriginDir, "refs/heads/" + Cart + "^", ct),
            Added = await _project.OriginGitAsync(["rev-list", "--count", start + "..refs/heads/" + Cart], ct),
        }.Should().BeEquivalentTo(new { Status = ReportStatus.Done, Local = owner, Parent = start, Added = "1" });
    }

    // AC-7d (1): a failed run with the named branch on the origin and the owner's local branch one commit ahead: neither moves, the
    // report stays failed with the "not changed" line, and the written file is on origin's zyggy/<id>.
    [Fact]
    public async Task ProcessOnceAsync_NamedRunFails_NamedBranchesUnchangedWorkOnOriginZyggyBranch()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await _project.GitAsync(["push", "-q", "origin", "main:refs/heads/" + Cart], ct);
        var named = await _project.ResolveAsync(_project.OriginDir, "refs/heads/" + Cart, ct);
        var owner = await OwnerCommitOnLocalBranchAsync(ct);
        var environment = Done("+src/cart.txt\tcart");
        environment["ZYGGY_FAKE_CLAUDE_SCENARIO"] = "error";
        environment["ZYGGY_FAKE_CLAUDE_EXIT"] = "1";
        await using var services = await StartAsync(environment, ct);
        var job = await SeedAndPullAsync(services, Cart, ct);

        // Act
        await ProcessAsync(services, ct);

        // Assert
        var report = await ReportForAsync(job, ct);
        new
        {
            report.Report!.Status,
            NotChanged = report.BodyText.Contains("\nbranch " + Cart + " not changed: run ended failed\n", StringComparison.Ordinal),
            Named = await _project.ResolveAsync(_project.OriginDir, "refs/heads/" + Cart, ct),
            Local = await _project.ResolveAsync(_project.Path, "refs/heads/" + Cart, ct),
            Work = await _project.OriginGitAsync(["show", "refs/heads/" + JobBranch(job) + ":src/cart.txt"], ct),
        }.Should().BeEquivalentTo(new { Status = ReportStatus.Failed, NotChanged = true, Named = named, Local = owner, Work = "cart" });
    }

    // AC-7d (2): a timed-out run with the named branch absent on the origin and locally: it stays absent in both, the report stays
    // timeout with the "not changed" line, and the written file is on origin's zyggy/<id>.
    [Fact]
    public async Task ProcessOnceAsync_NamedRunTimesOut_NamedBranchStillAbsentWorkOnOriginZyggyBranch()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var environment = Done("+src/cart.txt\thalf done");
        environment["ZYGGY_FAKE_CLAUDE_DELAY_MS"] = "30000";
        await using var services = await StartAsync(environment, ct, TimeSpan.FromSeconds(3));
        var job = await SeedAndPullAsync(services, Cart, ct);

        // Act
        await ProcessAsync(services, ct);

        // Assert
        var report = await ReportForAsync(job, ct);
        new
        {
            report.Report!.Status,
            NotChanged = report.BodyText.Contains("\nbranch " + Cart + " not changed: run ended timeout\n", StringComparison.Ordinal),
            Named = await _project.ResolveAsync(_project.OriginDir, "refs/heads/" + Cart, ct),
            Local = await _project.ResolveAsync(_project.Path, "refs/heads/" + Cart, ct),
            Work = await _project.OriginGitAsync(["show", "refs/heads/" + JobBranch(job) + ":src/cart.txt"], ct),
        }.Should().BeEquivalentTo(new { Status = ReportStatus.Timeout, NotChanged = true, Named = (string?)null, Local = (string?)null, Work = "half done" });
    }

    // AC-10: a second clone pushes to the named branch while the job runs; git refuses the job's non-forced push, the run ends
    // failed / git_error, the colleague's commit stays the named tip, and the job's work is on origin's zyggy/<id>.
    [Fact]
    public async Task ProcessOnceAsync_SecondClonePushesNamedBranchFirst_FailedGitErrorColleagueTipKeptWorkOnZyggyBranch()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var colleague = await ArrangeRaceAsync(ct);
        await using var services = await StartAsync(Done("+src/cart.txt\tcart"), ct);
        var job = await SeedAndPullAsync(services, Cart, ct);

        // Act
        await ProcessAsync(services, ct);

        // Assert
        var report = await ReportForAsync(job, ct);
        new
        {
            report.Report!.Status,
            report.Report.Reason,
            NotChanged = report.BodyText.Contains("\nbranch " + Cart + " not changed: push refused\n", StringComparison.Ordinal),
            Named = await _project.ResolveAsync(_project.OriginDir, "refs/heads/" + Cart, ct),
            Work = await _project.OriginGitAsync(["show", "refs/heads/" + JobBranch(job) + ":src/cart.txt"], ct),
        }.Should().BeEquivalentTo(new { Status = ReportStatus.Failed, Reason = "git_error", NotChanged = true, Named = colleague, Work = "cart" });
    }

    // AC-10: in the lost race, no git command the node runs in the project forces, resets, merges, rebases or switches a branch, and
    // the only one naming the branch is the single push zyggy/<id>:<name>.
    [Fact]
    public async Task ProcessOnceAsync_LostRace_NoForcingCommandAndOnlyThePushNamesTheBranch()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await ArrangeRaceAsync(ct);
        await using var services = await StartAsync(Done("+src/cart.txt\tcart"), ct);
        var job = await SeedAndPullAsync(services, Cart, ct);
        int before = _nodeHooks.Calls.Count;

        // Act
        await ProcessAsync(services, ct);

        // Assert
        var projectGit = _nodeHooks.Calls.Skip(before).Where(IsProjectGit).Select(c => c.Arguments).ToList();
        new
        {
            Forcing = projectGit.Where(a => a.Any(w => ForbiddenGitWords.Contains(w) || w.StartsWith('+'))).Select(a => string.Join(' ', a)),
            Naming = projectGit.Where(a => a.Any(w => w.Contains("refs/heads/" + Cart, StringComparison.Ordinal))).Select(a => a[^1]),
        }.Should().BeEquivalentTo(new
        {
            Forcing = Array.Empty<string>(),
            Naming = new[] { "refs/heads/" + JobBranch(job) + ":refs/heads/" + Cart },
        });
    }

    private Dictionary<string, string?> ClosedHealth => new() { ["ZYGGY_HEALTH_URL"] = $"http://127.0.0.1:{_closedPort}/health" };

    private static string JobBranch(SeededJob job) => "zyggy/" + job.Id.Value;

    private static Dictionary<string, string> Done(string edits) => new()
    {
        ["ZYGGY_FAKE_CLAUDE_SCENARIO"] = "done",
        ["ZYGGY_FAKE_CLAUDE_EDITS"] = edits,
    };

    private static async Task ProcessAsync(ServiceProvider services, CancellationToken ct) =>
        (await services.GetRequiredService<BusListener>().ProcessOnceAsync(ct)).Should().Be(1);

    private bool IsProjectGit(ProcessSpec spec) =>
        HookedProcessRunner.SubCommand(spec).Length > 0
        && Path.GetFileNameWithoutExtension(spec.FileName).Equals("git", StringComparison.OrdinalIgnoreCase)
        && (spec.WorkingDirectory.StartsWith(_project.Path, StringComparison.OrdinalIgnoreCase)
            || spec.WorkingDirectory.StartsWith(_project.NodeDir, StringComparison.OrdinalIgnoreCase));

    // The named branch exists on the origin at main; just before the node's push to it, a colleague's clone pushes one commit there.
    // Returns the colleague's commit.
    private async Task<string> ArrangeRaceAsync(CancellationToken ct)
    {
        await _project.GitAsync(["push", "-q", "origin", "main:refs/heads/" + Cart], ct);
        var other = await OtherCloneAsync(ct);
        var colleague = await CommitAsync(other, Cart, "colleague.txt", ct);
        var pushed = 0;
        _nodeHooks.BeforeRun = async spec =>
        {
            if (spec.Arguments.Contains("push") && spec.Arguments.Any(a => a.EndsWith(":refs/heads/" + Cart, StringComparison.Ordinal))
                && Interlocked.Exchange(ref pushed, 1) == 0)
            {
                await Bus.RunGitAsync(other, ["push", "-q", "origin", Cart], ct);
            }
        };
        return colleague;
    }

    // A second clone of the origin with its own fixture identity (a colleague), outside the project's checkout.
    private async Task<string> OtherCloneAsync(CancellationToken ct)
    {
        var other = Path.Combine(_project.Root, "other");
        await Bus.RunGitAsync(_project.Root, ["clone", "-q", _project.OriginDir, other], ct);
        foreach (string[] config in (string[][])[["user.name", "bob (fixture)"], ["user.email", "bob@example.invalid"], ["commit.gpgsign", "false"], ["core.autocrlf", "false"]])
        {
            await Bus.RunGitAsync(other, ["config", config[0], config[1]], ct);
        }

        return other;
    }

    // Creates (or continues) <branch> in <checkout> with one commit adding <file>, returns to main and returns the commit.
    private async Task<string> CommitAsync(string checkout, string branch, string file, CancellationToken ct)
    {
        await Bus.RunGitAsync(checkout, ["checkout", "-q", "-B", branch], ct);
        await File.WriteAllTextAsync(Path.Combine(checkout, file), file + "\n", ct);
        await Bus.RunGitAsync(checkout, ["add", "-A"], ct);
        await Bus.RunGitAsync(checkout, ["commit", "-q", "-m", "commit " + file], ct);
        var sha = await Bus.RunGitAsync(checkout, ["rev-parse", "HEAD"], ct);
        await Bus.RunGitAsync(checkout, ["checkout", "-q", "main"], ct);
        return sha;
    }

    // The owner's own unpushed commit on a local branch of the job's name, started at main; the checkout is back on main afterwards.
    private Task<string> OwnerCommitOnLocalBranchAsync(CancellationToken ct) => CommitAsync(_project.Path, Cart, "owner.txt", ct);

    // A pre-receive hook on the origin refusing updates of refs/heads/<Cart> only; every other ref (zyggy/<id>) is accepted.
    private Task RefuseNamedBranchOnOriginAsync(CancellationToken ct)
    {
        var path = Path.Combine(_project.OriginDir, "hooks", "pre-receive");
        var script = "#!/bin/sh\nwhile read old new ref; do\n  if [ \"$ref\" = \"refs/heads/" + Cart + "\" ]; then\n    echo 'pre-receive: named branch refused by fixture' >&2\n    exit 1\n  fi\ndone\nexit 0\n";
        return WriteHookAsync(path, script, ct);
    }

    private static async Task WriteHookAsync(string path, string script, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, script, ct);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private async Task<ServiceProvider> StartAsync(Dictionary<string, string> environment, CancellationToken ct, TimeSpan? timeout = null)
    {
        NodeConfiguration configuration = TestHost.NodeConfigurationFor(Bus, CliHarness.HomeLaptop);
        var services = TestHost.BuildRunnerListener(Bus, CliHarness.HomeLaptop, _store, FakeClaude.ExecutablePath, configuration, o =>
        {
            o.NodeDir = _project.NodeDir;
            o.TimeoutOverride = timeout ?? TimeSpan.FromSeconds(30);
            o.ModelEnvironment = environment;
        }, hooks: _nodeHooks);
        (await services.GetRequiredService<BusListener>().StartAsync(ct)).Should().Be(BusStartupResult.Clean);
        return services;
    }

    private async Task<SeededJob> SeedAndPullAsync(ServiceProvider services, string branch, CancellationToken ct)
    {
        var fields = new JobFields(_project.Path, Worktree: true, TimeoutMinutes: 2, AllowedTools: AllowedTools, Branch: branch);
        SeededJob job = await Bus.SeedSignedJobAsync(CliHarness.Central, CliHarness.HomeLaptop, TestKeys.Acme1, _store, "Change the shop.\n", fields: fields, ct: ct);
        (await services.GetRequiredService<IBusProvider>().PullAsync(ct)).Succeeded.Should().BeTrue();
        return job;
    }

    private async Task<Model> ReportForAsync(SeededJob job, CancellationToken ct)
    {
        string output = await Bus.RunGitAsync(Bus.BareDir, ["ls-tree", "--name-only", "main", BusPaths.Reports(CliHarness.Acme, CliHarness.HomeLaptop) + "/"], ct);
        var reports = new List<Model>();
        foreach (var path in output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(p => p.EndsWith(".md", StringComparison.Ordinal)))
        {
            EnvelopeResult parsed = await new EnvelopeSigner(_store).ParseAndVerifyAsync(await Bus.ShowBytesAsync(path, ct), CliHarness.Acme, ct);
            parsed.IsAccepted.Should().BeTrue(parsed.Rejection?.ToString());
            reports.Add(parsed.Envelope!);
        }

        return reports.Where(r => r.Header.InReplyTo == job.Id).Should().ContainSingle().Subject;
    }
}
