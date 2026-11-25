using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Envelope;
using Zyggy.Core.Git;
using Zyggy.Core.Jobs;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Jobs;

/// <summary>
/// Spec 61 slice 9: leftover worktrees are recovered, cleaned or listed, never lost; <c>worktree_add</c> says why. Real listener and job
/// runner, real <c>git</c> on a project whose <c>origin</c> is a bare repository, <c>tools/fake-claude</c>.
/// AC-27: the same job id met again with uncommitted work in its leftover folder. AC-28: <c>worktree add</c> refused or the folder held
/// (the failure of 2026-11-24: a re-run of the same id whose leftover folder could not be replaced). AC-29: the start-up clean-up.
/// </summary>
public sealed class LeftoverWorktreeTests : IAsyncLifetime
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName Central = MachineName.Parse("central");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly string[] AllowedTools = ["Read", "Edit"];

    private static readonly EnvelopeId Clean = EnvelopeId.Parse("01M8000000000000000000000A");

    private static readonly EnvelopeId Unpushed = EnvelopeId.Parse("01M8000000000000000000000B");

    private static readonly EnvelopeId Refused = EnvelopeId.Parse("01M8000000000000000000000C");

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

    // AC-27: the uncommitted file of the leftover is in a commit on zyggy/<id>, and the new run starts from it
    [Fact]
    public async Task SameJobIdMetAgain_LeftoverWithUncommittedFile_CommittedOnBranchAndRunStartsFromIt()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var services = Build("+src/second.txt\tsecond");
        var job = await SeedAndPullAsync(services, ct);
        await LeftoverAsync(job.Id, ct);
        await File.WriteAllTextAsync(Path.Combine(WorktreeFor(job.Id), "left.txt"), "unsaved work\n", ct);

        // Act
        await ProcessAsync(services, ct);

        // Assert
        new
        {
            (await SingleReportAsync(ct)).Report!.Status,
            Commits = await _project.OriginGitAsync(["log", "--format=%s", "main..refs/heads/" + Branch(job.Id)], ct),
            Left = await _project.OriginGitAsync(["show", "refs/heads/" + Branch(job.Id) + ":left.txt"], ct),
        }.Should().BeEquivalentTo(new
        {
            Status = ReportStatus.Done,
            Commits = "zyggy job " + job.Id.Value + "\nzyggy job " + job.Id.Value + " (recovered)",
            Left = "unsaved work",
        });
    }

    // AC-28 (the failure of 2026-11-24, cross-platform form): git refuses the add — the report carries git's first error line
    [Fact]
    public async Task SameJobIdMetAgain_BranchHeldByAnotherWorktree_GitErrorWithGitsLineModelNeverStarted()
    {
        // Arrange: the job's branch is checked out in a worktree git still knows, at another path.
        var ct = TestContext.Current.CancellationToken;
        await using var services = Build("+src/second.txt\tsecond");
        var job = await SeedAndPullAsync(services, ct);
        string elsewhere = Path.Combine(_project.Root, "elsewhere");
        await _project.GitAsync(["worktree", "add", "-q", "-b", Branch(job.Id), elsewhere, "main"], ct);

        // Act
        await ProcessAsync(services, ct);

        // Assert
        var report = await SingleReportAsync(ct);
        new
        {
            report.Report!.Reason,
            SaysWhy = report.BodyText.StartsWith("git_error: worktree_add (fatal: ", StringComparison.Ordinal) && report.BodyText.Contains(Branch(job.Id), StringComparison.Ordinal),
            Elsewhere = Directory.Exists(elsewhere),
            ModelRan = File.Exists(CapturePath),
        }.Should().BeEquivalentTo(new { Reason = "git_error", SaysWhy = true, Elsewhere = true, ModelRan = false }, report.BodyText);
    }

    // AC-28 and the failure of 2026-11-24 as it happened on Windows: the leftover folder of the same id is held open by a process — the
    // folder is kept, the report says why and where, and the leftover's work is committed on zyggy/<id> before anything is removed.
    [Fact]
    public async Task SameJobIdMetAgain_LeftoverHeldOpen_GitErrorSaysWhyFolderKeptWorkCommitted()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "A file held open blocks a delete on Windows only.");

        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var services = Build("+src/second.txt\tsecond");
        var job = await SeedAndPullAsync(services, ct);
        await LeftoverAsync(job.Id, ct);
        string held = Path.Combine(WorktreeFor(job.Id), "held.txt");
        await File.WriteAllTextAsync(held, "held work\n", ct);
        string body;
        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            // Act
            await ProcessAsync(services, ct);
            body = (await SingleReportAsync(ct)).BodyText;
        }

        // Assert
        new
        {
            SaysWhy = body.StartsWith("git_error: worktree_add (", StringComparison.Ordinal),
            KeptLine = body.Contains("worktree kept at " + WorktreeFor(job.Id) + "\n", StringComparison.Ordinal),
            Folder = Directory.Exists(WorktreeFor(job.Id)),
            Work = await _project.GitAsync(["show", "refs/heads/" + Branch(job.Id) + ":held.txt"], ct),
            ModelRan = File.Exists(CapturePath),
        }.Should().BeEquivalentTo(new { SaysWhy = true, KeptLine = true, Folder = true, Work = "held work", ModelRan = false }, body);
    }

    // AC-27/AC-28 (slice 9 correction): a leftover whose checkout is not zyggy/<id> is never committed or removed — its uncommitted work
    // stays where it is, the folder is kept and listed, and the job fails git_error naming the kept path; the model never starts.
    [Theory]
    [InlineData("checkout -q --detach", "checked out a detached HEAD, not zyggy/")]
    [InlineData("checkout -q -b other-work", "checked out refs/heads/other-work, not zyggy/")]
    public async Task SameJobIdMetAgain_LeftoverNotOnItsBranchWithUncommittedWork_KeptListedWorkUntouched(string checkout, string why)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var services = Build("+src/second.txt\tsecond");
        var job = await SeedAndPullAsync(services, ct);
        await LeftoverAsync(job.Id, ct);
        await _bus.RunGitAsync(WorktreeFor(job.Id), checkout.Split(' '), ct);
        await File.WriteAllTextAsync(Path.Combine(WorktreeFor(job.Id), "left.txt"), "unsaved work\n", ct);

        // Act
        await ProcessAsync(services, ct);

        // Assert
        var report = await SingleReportAsync(ct);
        new
        {
            report.Report!.Reason,
            SaysWhy = report.BodyText.StartsWith("git_error: worktree_add (" + why + job.Id.Value + ")\nworktree kept at " + WorktreeFor(job.Id) + "\n", StringComparison.Ordinal),
            Status = await _bus.RunGitAsync(WorktreeFor(job.Id), ["status", "--porcelain"], ct),
            Kept = LeftoverWorktrees.ReadKept(_project.NodeDir, Acme).Select(k => (k.Id, k.Path)).ToList(),
            ModelRan = File.Exists(CapturePath),
        }.Should().BeEquivalentTo(
            new { Reason = "git_error", SaysWhy = true, Status = "?? left.txt", Kept = new[] { (job.Id.Value, WorktreeFor(job.Id)) }, ModelRan = false },
            report.BodyText);
    }

    // AC-28 (slice 9 correction): a folder at the job's path that is not a worktree of the project is never deleted — kept and listed
    [Fact]
    public async Task SameJobIdMetAgain_FolderNotAWorktreeOfTheProject_KeptListedNothingDeleted()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var services = Build("+src/second.txt\tsecond");
        var job = await SeedAndPullAsync(services, ct);
        Directory.CreateDirectory(WorktreeFor(job.Id));
        await File.WriteAllTextAsync(Path.Combine(WorktreeFor(job.Id), "foreign.txt"), "not ours\n", ct);

        // Act
        await ProcessAsync(services, ct);

        // Assert
        var report = await SingleReportAsync(ct);
        new
        {
            report.Report!.Reason,
            SaysWhy = report.BodyText.StartsWith("git_error: worktree_add (not a git worktree)\nworktree kept at " + WorktreeFor(job.Id) + "\n", StringComparison.Ordinal),
            Foreign = await File.ReadAllTextAsync(Path.Combine(WorktreeFor(job.Id), "foreign.txt"), ct),
            Kept = LeftoverWorktrees.ReadKept(_project.NodeDir, Acme).Select(k => (k.Id, k.Path)).ToList(),
            ModelRan = File.Exists(CapturePath),
        }.Should().BeEquivalentTo(
            new { Reason = "git_error", SaysWhy = true, Foreign = "not ours\n", Kept = new[] { (job.Id.Value, WorktreeFor(job.Id)) }, ModelRan = false },
            report.BodyText);
    }

    // AC-29: a clean leftover (branch not ahead of what the origin has) is removed with its local branch
    [Fact]
    public async Task StartUp_CleanLeftover_FolderAndBranchRemoved()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await LeftoverAsync(Clean, ct);
        await using var services = Build(edits: null);

        // Act
        await StartAsync(services, ct);

        // Assert
        (await StateAsync(Clean, ct)).Should().BeEquivalentTo(new { Folder = false, Local = (string?)null, Remote = (string?)null });
    }

    // AC-29: a leftover with a commit and an uncommitted file the origin lacks is committed, pushed to origin/zyggy/<id>, then removed
    [Fact]
    public async Task StartUp_LeftoverWithUnpushedWork_PushedToOriginThenRemoved()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await LeftoverAsync(Unpushed, ct);
        await CommitInAsync(Unpushed, "first.txt", "first\n", ct);
        await File.WriteAllTextAsync(Path.Combine(WorktreeFor(Unpushed), "second.txt"), "second\n", ct);
        await using var services = Build(edits: null);

        // Act
        await StartAsync(services, ct);

        // Assert
        new
        {
            State = await StateAsync(Unpushed, ct),
            Second = await _project.OriginGitAsync(["show", "refs/heads/" + Branch(Unpushed) + ":second.txt"], ct),
            Kept = LeftoverWorktrees.ReadKept(_project.NodeDir, Acme).Count,
        }.Should().BeEquivalentTo(new
        {
            State = new { Folder = false, Local = (string?)null, Remote = "present" },
            Second = "second",
            Kept = 0,
        });
    }

    // AC-29: a leftover whose push fails is kept whole (folder and branch) and listed with why
    [Fact]
    public async Task StartUp_LeftoverWhosePushFails_KeptAndListed()
    {
        // Arrange: the origin already has zyggy/<id> with another commit, so the non-forced push is refused.
        var ct = TestContext.Current.CancellationToken;
        await _project.GitAsync(["branch", "other", "main"], ct);
        await _project.GitAsync(["worktree", "add", "-q", Path.Combine(_project.Root, "other"), "other"], ct);
        await File.WriteAllTextAsync(Path.Combine(_project.Root, "other", "other.txt"), "other\n", ct);
        await _bus.RunGitAsync(Path.Combine(_project.Root, "other"), ["add", "-A"], ct);
        await _bus.RunGitAsync(Path.Combine(_project.Root, "other"), ["commit", "-q", "-m", "other"], ct);
        await _project.GitAsync(["push", "-q", "origin", "refs/heads/other:refs/heads/" + Branch(Refused)], ct);
        await LeftoverAsync(Refused, ct);
        await CommitInAsync(Refused, "mine.txt", "mine\n", ct);
        await using var services = Build(edits: null);

        // Act
        await StartAsync(services, ct);

        // Assert
        var kept = LeftoverWorktrees.ReadKept(_project.NodeDir, Acme).Should().ContainSingle().Subject;
        new
        {
            Folder = Directory.Exists(WorktreeFor(Refused)),
            Mine = await _project.GitAsync(["show", "refs/heads/" + Branch(Refused) + ":mine.txt"], ct),
            kept.Id,
            kept.Path,
            Push = kept.Why.StartsWith("push: ", StringComparison.Ordinal),
        }.Should().BeEquivalentTo(new { Folder = true, Mine = "mine", Id = Refused.Value, Path = WorktreeFor(Refused), Push = true }, kept.Why);
    }

    // AC-29 guard: a folder whose id has a claim or a live run is never touched
    [Fact]
    public async Task Clean_FolderOfABusyId_Untouched()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await LeftoverAsync(Unpushed, ct);
        await File.WriteAllTextAsync(Path.Combine(WorktreeFor(Unpushed), "live.txt"), "the live run's file\n", ct);
        await using var services = Build(edits: null);
        var leftovers = new LeftoverWorktrees(services.GetRequiredService<GitClient>(), _project.NodeDir, Acme, new GitIdentity("zyggy (home-laptop)", "home-laptop@zyggy.org"), NullLogger<LeftoverWorktrees>.Instance);

        // Act
        await leftovers.CleanAsync(id => id == Unpushed.Value, ct);

        // Assert
        new
        {
            Live = await File.ReadAllTextAsync(Path.Combine(WorktreeFor(Unpushed), "live.txt"), ct),
            Status = await _bus.RunGitAsync(WorktreeFor(Unpushed), ["status", "--porcelain"], ct),
        }.Should().BeEquivalentTo(new { Live = "the live run's file\n", Status = "?? live.txt" });
    }

    // AC-29 edge: a leftover whose project folder no longer exists is kept and listed with "project not found"
    [Fact]
    public async Task StartUp_LeftoverOfAProjectThatIsGone_KeptProjectNotFound()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await LeftoverAsync(Clean, ct);
        await using var services = Build(edits: null);
        var leftovers = new LeftoverWorktrees(services.GetRequiredService<GitClient>(), _project.NodeDir, Acme, new GitIdentity("zyggy (home-laptop)", "home-laptop@zyggy.org"), NullLogger<LeftoverWorktrees>.Instance);
        Directory.Move(_project.Path, _project.Path + "-moved");

        // Act
        var kept = await leftovers.CleanAsync(_ => false, ct);

        // Assert
        (kept.Select(k => (k.Id, k.Why)).ToList(), Directory.Exists(WorktreeFor(Clean))).Should().BeEquivalentTo((new[] { (Clean.Value, "project not found") }, true));
    }

    private static string Branch(EnvelopeId id) => "zyggy/" + id.Value;

    private static async Task ProcessAsync(ServiceProvider services, CancellationToken ct) =>
        (await services.GetRequiredService<BusListener>().ProcessOnceAsync(ct)).Should().Be(1);

    private static async Task StartAsync(ServiceProvider services, CancellationToken ct)
    {
        BusListener listener = services.GetRequiredService<BusListener>();
        (await listener.StartAsync(ct)).Should().Be(BusStartupResult.Clean);
        await listener.ResumeClaimedAsync(ct);
    }

    private string WorktreeFor(EnvelopeId id) => TranscriptPaths.WorktreeFor(_project.NodeDir, Acme, id);

    private Task<string> LeftoverAsync(EnvelopeId id, CancellationToken ct) =>
        _project.GitAsync(["worktree", "add", "-q", "-b", Branch(id), WorktreeFor(id), "main"], ct);

    private async Task CommitInAsync(EnvelopeId id, string file, string content, CancellationToken ct)
    {
        await File.WriteAllTextAsync(Path.Combine(WorktreeFor(id), file), content, ct);
        await _bus.RunGitAsync(WorktreeFor(id), ["add", "-A"], ct);
        await _bus.RunGitAsync(WorktreeFor(id), ["commit", "-q", "-m", "earlier run"], ct);
    }

    private async Task<object> StateAsync(EnvelopeId id, CancellationToken ct) => new
    {
        Folder = Directory.Exists(WorktreeFor(id)),
        Local = await _project.ResolveAsync(_project.Path, "refs/heads/" + Branch(id), ct),
        Remote = await _project.ResolveAsync(_project.OriginDir, "refs/heads/" + Branch(id), ct) is null ? null : "present",
    };

    private ServiceProvider Build(string? edits)
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

        NodeConfiguration configuration = TestHost.NodeConfigurationFor(_bus, HomeLaptop);
        return TestHost.BuildRunnerListener(_bus, HomeLaptop, _store, FakeClaude.ExecutablePath, configuration, o =>
        {
            o.NodeDir = _project.NodeDir;
            o.TimeoutOverride = TimeSpan.FromSeconds(30);
            o.ModelEnvironment = environment;
        });
    }

    private async Task<SeededJob> SeedAndPullAsync(ServiceProvider services, CancellationToken ct)
    {
        (await services.GetRequiredService<BusListener>().StartAsync(ct)).Should().Be(BusStartupResult.Clean);
        var fields = new JobFields(_project.Path, Worktree: true, TimeoutMinutes: 2, AllowedTools: AllowedTools);
        SeededJob job = await _bus.SeedSignedJobAsync(Central, HomeLaptop, TestKeys.Acme1, _store, "Change the shop.\n", fields: fields, ct: ct);
        (await services.GetRequiredService<IBusProvider>().PullAsync(ct)).Succeeded.Should().BeTrue();
        return job;
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
