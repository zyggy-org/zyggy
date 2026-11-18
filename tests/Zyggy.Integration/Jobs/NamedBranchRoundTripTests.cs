using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Envelope;
using Zyggy.Core.Jobs;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Jobs;

/// <summary>
/// Spec 53 slice 5 (Wire, happy path): hand-written signed jobs carrying <c>branch</c> and <c>description</c> through the production
/// node composition root, real <c>git</c> on a project whose <c>origin</c> is a second bare repository, and <c>tools/fake-claude</c>
/// changing files. A first job creates the named branch on the origin, a second one continues it.
/// </summary>
public sealed class NamedBranchRoundTripTests : IAsyncLifetime
{
    private const string Cart = "feature/12-cart";

    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName Central = MachineName.Parse("central");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly string[] AllowedTools = ["Read", "Edit"];

    private static readonly string[] OnlyTotals = ["src/totals.txt"];

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

    // AC-3: a named done job whose branch is not on origin runs on zyggy/<id> from origin/main and creates the branch there; no local
    // branch <name> and no local zyggy/<id> remain.
    [Fact]
    public async Task ProcessOnceAsync_NamedBranchAbsentOnOrigin_BranchCreatedOnOriginFromMainNoLocalBranchesLeft()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var main = await _project.ResolveAsync(_project.OriginDir, "refs/heads/main", ct);
        await using var services = await StartAsync("+src/cart.txt\tcart", ct);
        var job = await SeedAndPullAsync(services, Cart, "Cart: slice 1", ct);

        // Act
        await ProcessAsync(services, ct);

        // Assert
        var report = (await ReportForAsync(job, ct)).Report!;
        new
        {
            report.Status,
            report.DiffRef,
            Parent = await _project.ResolveAsync(_project.OriginDir, "refs/heads/" + Cart + "^", ct),
            LocalNamed = await LocalBranchesAsync(Cart, ct),
            LocalJob = await LocalBranchesAsync(JobBranch(job), ct),
        }.Should().BeEquivalentTo(new
        {
            Status = ReportStatus.Done,
            DiffRef = await _project.ResolveAsync(_project.OriginDir, "refs/heads/" + Cart, ct),
            Parent = main,
            LocalNamed = string.Empty,
            LocalJob = string.Empty,
        });
    }

    // AC-11: after an accepted named push the job branch is deleted locally and never pushed under its own name.
    [Fact]
    public async Task ProcessOnceAsync_NamedJobDone_NoZyggyBranchLocallyOrOnOrigin()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var services = await StartAsync("+src/cart.txt\tcart", ct);
        var job = await SeedAndPullAsync(services, Cart, "Cart: slice 1", ct);

        // Act
        await ProcessAsync(services, ct);

        // Assert
        new
        {
            Local = await _project.ResolveAsync(_project.Path, "refs/heads/" + JobBranch(job), ct),
            Origin = await _project.ResolveAsync(_project.OriginDir, "refs/heads/" + JobBranch(job), ct),
            OriginZyggy = await _project.OriginGitAsync(["for-each-ref", "--format=%(refname)", "refs/heads/zyggy/"], ct),
            Folder = Directory.Exists(WorktreeFor(job)),
        }.Should().BeEquivalentTo(new { Local = (string?)null, Origin = (string?)null, OriginZyggy = string.Empty, Folder = false });
    }

    // AC-4: a second job on the same branch starts at origin/<branch>, so its commit sits on the first job's pushed tip.
    [Fact]
    public async Task ProcessOnceAsync_SecondJobSameBranch_ContinuesFirstJobsPushedTip()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;

        // Act
        var (first, second) = await RunTwoJobsAsync(ct);

        // Assert
        new
        {
            Parent = await _project.ResolveAsync(_project.OriginDir, "refs/heads/" + Cart + "^", ct),
            Subjects = await _project.OriginGitAsync(["log", "--format=%s", "main..refs/heads/" + Cart], ct),
            SecondDiffRef = (await ReportForAsync(second, ct)).Report!.DiffRef,
        }.Should().BeEquivalentTo(new
        {
            Parent = (await ReportForAsync(first, ct)).Report!.DiffRef,
            Subjects = "Cart: slice 2\nCart: slice 1",
            SecondDiffRef = await _project.ResolveAsync(_project.OriginDir, "refs/heads/" + Cart, ct),
        });
    }

    // AC-7: files_changed compares the start tip with the final tip, so the continuing job lists only its own file.
    [Fact]
    public async Task ProcessOnceAsync_SecondJobSameBranch_FilesChangedListsOnlyItsOwnFile()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;

        // Act
        var (_, second) = await RunTwoJobsAsync(ct);

        // Assert
        (await ReportForAsync(second, ct)).Report!.FilesChanged.Should().Equal(OnlyTotals);
    }

    // AC-5: the node's commit carries the description as subject and "zyggy job <id>" as body, read from the origin.
    [Fact]
    public async Task ProcessOnceAsync_NamedJobWithDescription_CommitSubjectIsDescriptionBodyIsJobId()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var services = await StartAsync("+src/cart.txt\tcart", ct);
        var job = await SeedAndPullAsync(services, Cart, "Cart: slice 1", ct);

        // Act
        await ProcessAsync(services, ct);

        // Assert
        (await _project.OriginGitAsync(["log", "-1", "--format=%B", "refs/heads/" + Cart], ct))
            .Should().Be("Cart: slice 1\n\nzyggy job " + job.Id.Value);
    }

    // AC-5: without a description the message stays "zyggy job <id>".
    [Fact]
    public async Task ProcessOnceAsync_NamedJobWithoutDescription_CommitMessageIsJobIdOnly()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var services = await StartAsync("+src/cart.txt\tcart", ct);
        var job = await SeedAndPullAsync(services, Cart, description: null, ct);

        // Act
        await ProcessAsync(services, ct);

        // Assert
        (await _project.OriginGitAsync(["log", "-1", "--format=%B", "refs/heads/" + Cart], ct))
            .Should().Be("zyggy job " + job.Id.Value);
    }

    // AC-5 (spec 53 D4): a description without a branch reaches the commit of the job's own zyggy/<id> branch.
    [Fact]
    public async Task ProcessOnceAsync_UnnamedJobWithDescription_ZyggyBranchCommitSubjectIsDescription()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var services = await StartAsync("+src/cart.txt\tcart", ct);
        var job = await SeedAndPullAsync(services, branch: null, "Cart: notes", ct);

        // Act
        await ProcessAsync(services, ct);

        // Assert
        (await _project.OriginGitAsync(["log", "-1", "--format=%B", "refs/heads/" + JobBranch(job)], ct))
            .Should().Be("Cart: notes\n\nzyggy job " + job.Id.Value);
    }

    // AC-6: the body ends with the named branch's line and the description line; no "behind" line while the branch is not behind.
    [Fact]
    public async Task ProcessOnceAsync_NamedJobNotBehindMain_BodyEndsWithPushedAndDescriptionLinesOnly()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var services = await StartAsync("+src/cart.txt\tcart", ct);
        var job = await SeedAndPullAsync(services, Cart, "Cart: slice 1", ct);

        // Act
        await ProcessAsync(services, ct);

        // Assert
        var report = await ReportForAsync(job, ct);
        report.BodyText.Should().EndWith(
            "\nbranch " + Cart + " pushed at " + report.Report!.DiffRef + "\ndescription: Cart: slice 1\n");
    }

    // AC-6: when origin/main moved on after the branch was started, the continuing job's report says how far behind the pushed branch is.
    [Fact]
    public async Task ProcessOnceAsync_MainMovedOnOriginBeforeSecondJob_BodyEndsWithBehindLine()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var services = await StartAsync("+src/cart.txt\tcart", ct);
        await SeedAndPullAsync(services, Cart, "Cart: slice 1", ct);
        await ProcessAsync(services, ct);
        await AdvanceMainOnOriginAsync(ct);
        WithEdits(services, "+src/totals.txt\ttotals");
        var second = await SeedAndPullAsync(services, Cart, "Cart: slice 2", ct);

        // Act
        await ProcessAsync(services, ct);

        // Assert
        var report = await ReportForAsync(second, ct);
        report.BodyText.Should().EndWith(
            "\nbranch " + Cart + " pushed at " + report.Report!.DiffRef + "\ndescription: Cart: slice 2\nbranch " + Cart + " is 1 commits behind origin/main\n");
    }

    private static string JobBranch(SeededJob job) => "zyggy/" + job.Id.Value;

    private static async Task ProcessAsync(ServiceProvider services, CancellationToken ct) =>
        (await services.GetRequiredService<BusListener>().ProcessOnceAsync(ct)).Should().Be(1);

    // fake-claude reads its edits from the model environment of each run, so a later job of the same host can change them.
    private static void WithEdits(ServiceProvider services, string edits)
    {
        var options = services.GetRequiredService<IOptions<JobRunnerOptions>>().Value;
        options.ModelEnvironment = new Dictionary<string, string>(options.ModelEnvironment) { ["ZYGGY_FAKE_CLAUDE_EDITS"] = edits };
    }

    private string WorktreeFor(SeededJob job) => TranscriptPaths.WorktreeFor(_project.NodeDir, Acme, job.Id);

    private async Task<(SeededJob First, SeededJob Second)> RunTwoJobsAsync(CancellationToken ct)
    {
        await using var services = await StartAsync("+src/cart.txt\tcart", ct);
        var first = await SeedAndPullAsync(services, Cart, "Cart: slice 1", ct);
        await ProcessAsync(services, ct);
        WithEdits(services, "+src/totals.txt\ttotals");
        var second = await SeedAndPullAsync(services, Cart, "Cart: slice 2", ct);
        await ProcessAsync(services, ct);
        return (first, second);
    }

    private async Task<ServiceProvider> StartAsync(string edits, CancellationToken ct)
    {
        var environment = new Dictionary<string, string>
        {
            ["ZYGGY_FAKE_CLAUDE_SCENARIO"] = "done",
            ["ZYGGY_FAKE_CLAUDE_CAPTURE"] = CapturePath,
            ["ZYGGY_FAKE_CLAUDE_EDITS"] = edits,
        };
        NodeConfiguration configuration = TestHost.NodeConfigurationFor(_bus, HomeLaptop);
        var services = TestHost.BuildRunnerListener(_bus, HomeLaptop, _store, FakeClaude.ExecutablePath, configuration, o =>
        {
            o.NodeDir = _project.NodeDir;
            o.TimeoutOverride = TimeSpan.FromSeconds(30);
            o.ModelEnvironment = environment;
        });
        (await services.GetRequiredService<BusListener>().StartAsync(ct)).Should().Be(BusStartupResult.Clean);
        return services;
    }

    private async Task<SeededJob> SeedAndPullAsync(ServiceProvider services, string? branch, string? description, CancellationToken ct)
    {
        var fields = new JobFields(_project.Path, Worktree: true, TimeoutMinutes: 2, AllowedTools: AllowedTools, Branch: branch, Description: description);
        SeededJob job = await _bus.SeedSignedJobAsync(Central, HomeLaptop, TestKeys.Acme1, _store, "Change the shop.\n", fields: fields, ct: ct);
        (await services.GetRequiredService<IBusProvider>().PullAsync(ct)).Succeeded.Should().BeTrue();
        return job;
    }

    // The owner lands one commit on main on the origin from a second clone; the project's own checkout is not touched.
    private async Task AdvanceMainOnOriginAsync(CancellationToken ct)
    {
        var other = _scratch.File("other");
        await _bus.RunGitAsync(_scratch.Path, ["clone", "-q", _project.OriginDir, other], ct);
        foreach (string[] config in (string[][])[["user.name", "bob (fixture)"], ["user.email", "bob@example.invalid"], ["commit.gpgsign", "false"]])
        {
            await _bus.RunGitAsync(other, ["config", config[0], config[1]], ct);
        }

        await File.WriteAllTextAsync(Path.Combine(other, "CHANGELOG.md"), "main moved on\n", ct);
        await _bus.RunGitAsync(other, ["add", "-A"], ct);
        await _bus.RunGitAsync(other, ["commit", "-q", "-m", "main moved on"], ct);
        await _bus.RunGitAsync(other, ["push", "-q", "origin", "main"], ct);
    }

    private Task<string> LocalBranchesAsync(string name, CancellationToken ct) =>
        _project.GitAsync(["for-each-ref", "--format=%(refname)", "refs/heads/" + name], ct);

    private async Task<Model> ReportForAsync(SeededJob job, CancellationToken ct)
    {
        string output = await _bus.RunGitAsync(_bus.BareDir, ["ls-tree", "--name-only", "main", BusPaths.Reports(Acme, HomeLaptop) + "/"], ct);
        var reports = new List<Model>();
        foreach (var path in output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(p => p.EndsWith(".md", StringComparison.Ordinal)))
        {
            EnvelopeResult parsed = await new EnvelopeSigner(_store).ParseAndVerifyAsync(await _bus.ShowBytesAsync(path, ct), Acme, ct);
            parsed.IsAccepted.Should().BeTrue(parsed.Rejection?.ToString());
            reports.Add(parsed.Envelope!);
        }

        return reports.Where(r => r.Header.InReplyTo == job.Id).Should().ContainSingle().Subject;
    }
}
