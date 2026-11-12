using System.Text;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Envelope;
using Zyggy.Core.Git;
using Zyggy.Core.Jobs;
using Zyggy.Core.Processes;
using Zyggy.Core.Registry;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Jobs;

/// <summary>
/// Spec 16 slice 3: worktree jobs through the real listener and job runner, real <c>git</c> on a project whose <c>origin</c> is a second
/// bare repository, and <c>tools/fake-claude</c> changing files in its working directory.
/// </summary>
public sealed class WorktreeRoundTripTests : IAsyncLifetime
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName Central = MachineName.Parse("central");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly string[] AllowedTools = ["Read", "Edit"];

    private static readonly string[] AddEditDelete = ["README.md", "src/a.txt", "src/new.txt"];

    private static readonly string[] OnlyShop = ["shop"];

    private readonly BusRepoFixture _bus = new(Acme);

    private readonly ScratchDirectory _scratch = new();

    private InMemorySecretStore _store = null!;

    private WorktreeProject _project = null!;

    private string CapturePath => _scratch.File("capture.bin");

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await _bus.InitializeAsync();
        await _bus.SeedTenantAsync(Acme, [Central, HomeLaptop], ct);
        _store = await TestHost.StoreAsync(Acme, ct);
        _project = await WorktreeProject.CreateAsync(_bus, ct);
    }

    public async ValueTask DisposeAsync()
    {
        _project?.Dispose();
        _scratch.Dispose();
        await _bus.DisposeAsync();
    }

    [Fact]
    public async Task ProcessOnceAsync_WorktreeJob_FakeClaudeRanInWorktreeFolder()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var services = Build("+src/new.txt\tnew");
        var job = await SeedAndPullAsync(services, worktree: true, ct);

        // Act
        await ProcessAsync(services, ct);

        // Assert
        FakeClaude.ReadCapture(CapturePath).WorkingDirectory.Should().Be(WorktreeFor(job));
    }

    [Fact]
    public async Task ProcessOnceAsync_EditsLeft_DoneWithDiffRefEqualToBranchOnOriginBasedOnMain()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var services = Build("+src/new.txt\tnew");
        var job = await SeedAndPullAsync(services, worktree: true, ct);
        var main = await _project.ResolveAsync(_project.OriginDir, "refs/heads/main", ct);

        // Act
        await ProcessAsync(services, ct);

        // Assert
        var report = (await SingleReportAsync(ct)).Report!;
        new
        {
            report.Status,
            report.DiffRef,
            Parent = await _project.ResolveAsync(_project.OriginDir, Branch(job) + "^", ct),
        }.Should().BeEquivalentTo(new
        {
            Status = ReportStatus.Done,
            DiffRef = await _project.ResolveAsync(_project.OriginDir, "refs/heads/" + Branch(job), ct),
            Parent = main,
        });
    }

    [Fact]
    public async Task ProcessOnceAsync_EditsLeftAndFailingPreCommitHook_NodeCommitWithMachineIdentityConfigUntouched()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await _project.AddFailingPreCommitHookAsync(ct);
        var config = await File.ReadAllBytesAsync(Path.Combine(_project.Path, ".git", "config"), ct);
        await using var services = Build("+src/new.txt\tnew");
        var job = await SeedAndPullAsync(services, worktree: true, ct);

        // Act
        await ProcessAsync(services, ct);

        // Assert
        new
        {
            Commit = await _project.OriginGitAsync(["log", "-1", "--format=%an|%ae|%s", "refs/heads/" + Branch(job)], ct),
            ConfigUnchanged = (await File.ReadAllBytesAsync(Path.Combine(_project.Path, ".git", "config"), ct)).SequenceEqual(config),
        }.Should().BeEquivalentTo(new { Commit = "zyggy (home-laptop)|home-laptop@zyggy.org|zyggy job " + job.Id.Value, ConfigUnchanged = true });
    }

    [Fact]
    public async Task ProcessOnceAsync_AddEditDelete_FilesChangedListsExactlyThosePaths()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var services = Build("+src/new.txt\tnew\n+README.md\t# shop, changed\n-src/a.txt");
        await SeedAndPullAsync(services, worktree: true, ct);

        // Act
        await ProcessAsync(services, ct);

        // Assert
        (await SingleReportAsync(ct)).Report!.FilesChanged.Should().Equal(AddEditDelete);
    }

    [Fact]
    public async Task ProcessOnceAsync_NothingChanged_DoneNoFilesNoDiffRefNoBranchNoFolder()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var services = Build(edits: null);
        var job = await SeedAndPullAsync(services, worktree: true, ct);

        // Act
        await ProcessAsync(services, ct);

        // Assert
        var report = (await SingleReportAsync(ct)).Report!;
        new
        {
            report.Status,
            report.FilesChanged,
            report.DiffRef,
            Local = await _project.ResolveAsync(_project.Path, "refs/heads/" + Branch(job), ct),
            Remote = await _project.ResolveAsync(_project.OriginDir, "refs/heads/" + Branch(job), ct),
            Folder = Directory.Exists(WorktreeFor(job)),
        }.Should().BeEquivalentTo(new
        {
            Status = ReportStatus.Done,
            FilesChanged = Array.Empty<string>(),
            DiffRef = (string?)null,
            Local = (string?)null,
            Remote = (string?)null,
            Folder = false,
        });
    }

    [Fact]
    public async Task ProcessOnceAsync_DoneAndPushed_WorktreeGoneFromDiskAndGitAndLocalBranchDeleted()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var services = Build("+src/new.txt\tnew");
        var job = await SeedAndPullAsync(services, worktree: true, ct);

        // Act
        await ProcessAsync(services, ct);

        // Assert
        (await CleanedUpAsync(job, ct)).Should().BeEquivalentTo(new { Folder = false, Listed = false, Local = (string?)null });
    }

    [Fact]
    public async Task ProcessOnceAsync_ModelWritesThenHangs_TimeoutWithWorkPushedAndWorktreeRemoved()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var services = Build("+src/partial.txt\thalf done", delayMs: 30_000, timeout: TimeSpan.FromSeconds(3));
        var job = await SeedAndPullAsync(services, worktree: true, ct);

        // Act
        await ProcessAsync(services, ct);

        // Assert
        var report = (await SingleReportAsync(ct)).Report!;
        new
        {
            report.Status,
            report.DiffRef,
            File = await _project.OriginGitAsync(["show", "refs/heads/" + Branch(job) + ":src/partial.txt"], ct),
            CleanedUp = await CleanedUpAsync(job, ct),
        }.Should().BeEquivalentTo(new
        {
            Status = ReportStatus.Timeout,
            DiffRef = await _project.ResolveAsync(_project.OriginDir, "refs/heads/" + Branch(job), ct),
            File = "half done",
            CleanedUp = new { Folder = false, Listed = false, Local = (string?)null },
        });
    }

    [Fact]
    public async Task ProcessOnceAsync_OriginRefusesPush_FailedGitErrorLocalBranchKeepsCommit()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await _project.AddRefusingOriginAsync(ct);
        await using var services = Build("+src/new.txt\tnew");
        var job = await SeedAndPullAsync(services, worktree: true, ct);

        // Act
        await ProcessAsync(services, ct);

        // Assert
        var report = await SingleReportAsync(ct);
        new
        {
            report.Report!.Status,
            report.Report.Reason,
            report.Report.DiffRef,
            KeptLine = report.BodyText.EndsWith("branch " + Branch(job) + " kept locally, not pushed: push\n", StringComparison.Ordinal),
            LocalCommit = await _project.GitAsync(["log", "-1", "--format=%s", "refs/heads/" + Branch(job)], ct),
            Folder = Directory.Exists(WorktreeFor(job)),
        }.Should().BeEquivalentTo(new
        {
            Status = ReportStatus.Failed,
            Reason = "git_error",
            DiffRef = (string?)null,
            KeptLine = true,
            LocalCommit = "zyggy job " + job.Id.Value,
            Folder = false,
        });
    }

    [Fact]
    public async Task ProcessOnceAsync_ProjectWithoutRemote_FailedGitErrorLocalBranchKept()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await _project.GitAsync(["remote", "remove", "origin"], ct);
        await using var services = Build("+src/new.txt\tnew");
        var job = await SeedAndPullAsync(services, worktree: true, ct);

        // Act
        await ProcessAsync(services, ct);

        // Assert
        var report = (await SingleReportAsync(ct)).Report!;
        new { report.Status, report.Reason, report.DiffRef, Kept = await _project.ResolveAsync(_project.Path, "refs/heads/" + Branch(job), ct) is not null }
            .Should().BeEquivalentTo(new { Status = ReportStatus.Failed, Reason = "git_error", DiffRef = (string?)null, Kept = true });
    }

    [Fact]
    public async Task ProcessOnceAsync_WorktreeCannotBeCreated_FailedGitErrorModelNeverStarted()
    {
        // Arrange: a file where the worktree folder must go makes `git worktree add` fail.
        var ct = TestContext.Current.CancellationToken;
        await using var services = Build("+src/new.txt\tnew");
        var job = await SeedAndPullAsync(services, worktree: true, ct);
        Directory.CreateDirectory(Path.GetDirectoryName(WorktreeFor(job))!);
        await File.WriteAllTextAsync(WorktreeFor(job), "in the way\n", ct);

        // Act
        await ProcessAsync(services, ct);

        // Assert
        var report = await SingleReportAsync(ct);
        new { report.Report!.Status, report.Report.Reason, report.BodyText, report.Report.FilesChanged, ModelRan = File.Exists(CapturePath) }
            .Should().BeEquivalentTo(new { Status = ReportStatus.Failed, Reason = "git_error", BodyText = "git_error: worktree_add\n", FilesChanged = Array.Empty<string>(), ModelRan = false });
    }

    [Fact]
    public async Task ProcessOnceAsync_InterruptedEarlierRun_FolderReplacedBranchReusedBothCommitsPushed()
    {
        // Arrange: an earlier run of the same job left its folder and a branch with one commit.
        var ct = TestContext.Current.CancellationToken;
        await using var services = Build("+src/second.txt\tsecond");
        var job = await SeedAndPullAsync(services, worktree: true, ct);
        await _project.GitAsync(["worktree", "add", "-q", "-b", Branch(job), WorktreeFor(job), "main"], ct);
        await File.WriteAllTextAsync(Path.Combine(WorktreeFor(job), "first.txt"), "first\n", ct);
        await _bus.RunGitAsync(WorktreeFor(job), ["add", "-A"], ct);
        await _bus.RunGitAsync(WorktreeFor(job), ["commit", "-q", "-m", "earlier run"], ct);

        // Act
        await ProcessAsync(services, ct);

        // Assert
        new
        {
            (await SingleReportAsync(ct)).Report!.Status,
            Commits = await _project.OriginGitAsync(["log", "--format=%s", "main..refs/heads/" + Branch(job)], ct),
        }.Should().BeEquivalentTo(new { Status = ReportStatus.Done, Commits = "zyggy job " + job.Id.Value + "\nearlier run" });
    }

    [Fact]
    public async Task ProcessOnceAsync_OwnerHasDirtyCheckout_CheckoutUntouched()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(_project.Path, "README.md"), "# shop, owner's unsaved work\n", ct);
        await File.WriteAllTextAsync(Path.Combine(_project.Path, "notes.txt"), "owner's notes\n", ct);
        var before = await CheckoutStateAsync(ct);
        await using var services = Build("+src/new.txt\tnew\n+README.md\tjob's version");
        await SeedAndPullAsync(services, worktree: true, ct);

        // Act
        await ProcessAsync(services, ct);

        // Assert
        (await CheckoutStateAsync(ct)).Should().Be(before);
    }

    [Fact]
    public async Task ProcessOnceAsync_InPlaceJob_NoJobBranchAndNoWorktreesFolder()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var services = Build(edits: null);
        await SeedAndPullAsync(services, worktree: false, ct);

        // Act
        await ProcessAsync(services, ct);

        // Assert
        new { Branches = await _project.GitAsync(["branch", "--list", "zyggy/*"], ct), Folder = Directory.Exists(Path.Combine(_project.NodeDir, "worktrees")) }
            .Should().BeEquivalentTo(new { Branches = string.Empty, Folder = false });
    }

    [Fact]
    public async Task DiscoveryScan_DuringWorktreeRun_PublishesNoProjectForWorktreeFolder()
    {
        // Arrange: the fake model pauses inside the worktree while a scan of the project's parent runs.
        var ct = TestContext.Current.CancellationToken;
        await using var services = Build("+src/new.txt\tnew", delayMs: 4_000);
        var job = await SeedAndPullAsync(services, worktree: true, ct);
        var run = ProcessAsync(services, ct);
        while (!File.Exists(CapturePath) && !run.IsCompleted)
        {
            await Task.Delay(50, ct);
        }

        var git = new GitClient(new ProcessRunner(TimeProvider.System), new GitClientOptions(), TimeProvider.System);
        var scanner = new DiscoveryScanner(git, null, new DiscoveryOptions(), TimeProvider.System, NullLogger<DiscoveryScanner>.Instance);

        // Act
        var scan = await scanner.ScanAsync([_project.Root], DiscoveryDetail.Full, ct);

        // Assert
        await run;
        new { WorktreeExisted = Directory.Exists(Path.Combine(WorktreeFor(job), ".claude")) || File.Exists(CapturePath), Projects = scan.Projects.Select(p => p.Name).ToList() }
            .Should().BeEquivalentTo(new { WorktreeExisted = true, Projects = OnlyShop });
    }

    private static string Branch(SeededJob job) => "zyggy/" + job.Id.Value;

    private static async Task ProcessAsync(ServiceProvider services, CancellationToken ct) =>
        (await services.GetRequiredService<BusListener>().ProcessOnceAsync(ct)).Should().Be(1);

    private string WorktreeFor(SeededJob job) => TranscriptPaths.WorktreeFor(_project.NodeDir, Acme, job.Id);

    private ServiceProvider Build(string? edits, int? delayMs = null, TimeSpan? timeout = null)
    {
        var environment = new Dictionary<string, string>
        {
            ["ZYGGY_FAKE_CLAUDE_SCENARIO"] = "done",
            ["ZYGGY_FAKE_CLAUDE_CAPTURE"] = CapturePath,
        };
        if (edits is not null)
        {
            environment["ZYGGY_FAKE_CLAUDE_EDITS"] = edits;
        }

        if (delayMs is { } delay)
        {
            environment["ZYGGY_FAKE_CLAUDE_DELAY_MS"] = delay.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        NodeConfiguration configuration = TestHost.NodeConfigurationFor(_bus, HomeLaptop);
        return TestHost.BuildRunnerListener(_bus, HomeLaptop, _store, FakeClaude.ExecutablePath, configuration, o =>
        {
            o.NodeDir = _project.NodeDir;
            o.TimeoutOverride = timeout ?? TimeSpan.FromSeconds(30);
            o.ModelEnvironment = environment;
        });
    }

    private async Task<SeededJob> SeedAndPullAsync(ServiceProvider services, bool worktree, CancellationToken ct)
    {
        (await services.GetRequiredService<BusListener>().StartAsync(ct)).Should().Be(BusStartupResult.Clean);
        var fields = new JobFields(_project.Path, Worktree: worktree, TimeoutMinutes: 2, AllowedTools: AllowedTools);
        SeededJob job = await _bus.SeedSignedJobAsync(Central, HomeLaptop, TestKeys.Acme1, _store, "Change the shop.\n", fields: fields, ct: ct);
        (await services.GetRequiredService<IBusProvider>().PullAsync(ct)).Succeeded.Should().BeTrue();
        return job;
    }

    private async Task<object> CleanedUpAsync(SeededJob job, CancellationToken ct)
    {
        var listed = await _project.GitAsync(["worktree", "list", "--porcelain"], ct);
        return new
        {
            Folder = Directory.Exists(WorktreeFor(job)),
            Listed = listed.Contains(job.Id.Value, StringComparison.Ordinal),
            Local = await _project.ResolveAsync(_project.Path, "refs/heads/" + Branch(job), ct),
        };
    }

    private async Task<string> CheckoutStateAsync(CancellationToken ct)
    {
        var state = new StringBuilder();
        state.AppendLine(await _project.GitAsync(["symbolic-ref", "HEAD"], ct));
        state.AppendLine(await _project.GitAsync(["rev-parse", "HEAD"], ct));
        state.AppendLine(await _project.GitAsync(["status", "--porcelain", "--untracked-files=all"], ct));
        state.AppendLine(await File.ReadAllTextAsync(Path.Combine(_project.Path, "README.md"), ct));
        return state.ToString();
    }

    private async Task<Model> SingleReportAsync(CancellationToken ct)
    {
        string output = await _bus.RunGitAsync(_bus.BareDir, ["ls-tree", "--name-only", "main", BusPaths.Reports(Acme, HomeLaptop) + "/"], ct);
        string path = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(p => p.EndsWith(".md", StringComparison.Ordinal)).Should().ContainSingle().Subject;
        EnvelopeResult parsed = await new EnvelopeSigner(_store).ParseAndVerifyAsync(await _bus.ShowBytesAsync(path, ct), Acme, ct);
        parsed.IsAccepted.Should().BeTrue(parsed.Rejection?.ToString());
        return parsed.Envelope!;
    }
}
