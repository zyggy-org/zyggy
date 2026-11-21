using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Envelope;
using Zyggy.Core.Registry;
using Zyggy.Integration.Cli;
using Zyggy.Integration.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Jobs;

/// <summary>
/// Spec 18 slice 8 (AC-18): a job under a mandate with <c>base</c> runs in a worktree started from that branch of the project's origin,
/// pushes <c>zyggy/&lt;id&gt;</c> and fast-forwards <c>base</c>; when <c>base</c> moved meanwhile the origin's <c>base</c> is kept and the
/// report says so; the project checkout is untouched either way. Central grants and submits through the real verbs, <c>home-laptop</c>
/// runs the production listener and job runner with <c>tools/fake-claude</c> editing the worktree, the project has a bare origin.
/// </summary>
public sealed class MandateBranchTests : IAsyncLifetime
{
    private const string Project = "shop";

    private const string Feature = "feature/shop";

    private CliHarness _harness = null!;

    private WorktreeProject _project = null!;

    private string CapturePath => _harness.Scratch.File("capture.bin");

    private static string LaptopReports => BusPaths.Reports(CliHarness.Acme, CliHarness.HomeLaptop);

    public async ValueTask InitializeAsync()
    {
        _harness = await CliHarness.CreateAsync(CancellationToken.None);
        _project = await WorktreeProject.CreateAsync(_harness.Bus, CancellationToken.None);
        var registry = new RegistryDocument(CliHarness.Acme, CliHarness.HomeLaptop, RegistryOs.Windows, DateTimeOffset.UtcNow.AddMinutes(-1), null, null, [])
        {
            Capabilities = [RegistryDocument.SupervisionCapability],
        };
        await _harness.Bus.SeedRawFileAsync(BusPaths.Registry(CliHarness.Acme, CliHarness.HomeLaptop), registry.Serialize(), "seed: home-laptop registry", CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        _project?.Dispose();
        await _harness.DisposeAsync();
    }

    // AC-18
    [Fact]
    public async Task Run_WithBase_WorktreeStartsFromTheBranchOnOrigin()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string start = await _project.AddBranchAsync(Feature, ct);
        await using ServiceProvider laptop = await LaptopAsync(ct);
        string job = await SubmitAsync(await GrantAsync(ct), Feature, ct);

        // Act
        await ProcessAsync(laptop, ct);

        // Assert
        (await _project.ResolveAsync(_project.OriginDir, "refs/heads/zyggy/" + job + "^", ct)).Should().Be(start);
    }

    // AC-18
    [Fact]
    public async Task Run_WithBase_PushesItsBranchAndFastForwardsBase_ReportSaysSo()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await _project.AddBranchAsync(Feature, ct);
        await using ServiceProvider laptop = await LaptopAsync(ct);
        string job = await SubmitAsync(await GrantAsync(ct), Feature, ct);

        // Act
        await ProcessAsync(laptop, ct);

        // Assert
        Model report = await SingleReportAsync(ct);
        string? tip = await _project.ResolveAsync(_project.OriginDir, "refs/heads/zyggy/" + job, ct);
        new
        {
            report.Report!.Status,
            report.Report.DiffRef,
            Base = await _project.ResolveAsync(_project.OriginDir, "refs/heads/" + Feature, ct),
            SaysSo = report.BodyText.Contains("branch " + Feature + " fast-forwarded to " + tip, StringComparison.Ordinal),
        }.Should().BeEquivalentTo(new { Status = ReportStatus.Done, DiffRef = tip, Base = tip, SaysSo = true });
    }

    // AC-18
    [Fact]
    public async Task Run_BaseMovedMeanwhile_BaseKeptOnOrigin_ItsBranchPushed_DoneAndReportSaysSo()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await _project.AddBranchAsync(Feature, ct);
        await _project.AddOriginThatMovesAsync(Feature, ct);
        await using ServiceProvider laptop = await LaptopAsync(ct);
        string job = await SubmitAsync(await GrantAsync(ct), Feature, ct);

        // Act
        await ProcessAsync(laptop, ct);

        // Assert
        Model report = await SingleReportAsync(ct);
        string? tip = await _project.ResolveAsync(_project.OriginDir, "refs/heads/zyggy/" + job, ct);
        new
        {
            report.Report!.Status,
            report.Report.DiffRef,
            BaseIsTheMove = await _project.OriginGitAsync(["log", "-1", "--format=%s", "refs/heads/" + Feature], ct),
            SaysSo = report.BodyText.Contains("branch " + Feature + " not updated (moved meanwhile); the result is zyggy/" + job, StringComparison.Ordinal),
        }.Should().BeEquivalentTo(new { Status = ReportStatus.Done, DiffRef = tip, BaseIsTheMove = "moved", SaysSo = true });
    }

    // AC-18
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_WithBase_ProjectCheckoutHeadAndWorkingTreeUnchanged(bool moved)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await _project.AddBranchAsync(Feature, ct);
        if (moved)
        {
            await _project.AddOriginThatMovesAsync(Feature, ct);
        }

        await File.WriteAllTextAsync(Path.Combine(_project.Path, "README.md"), "# shop, the owner's own edit\n", ct);
        var before = await CheckoutAsync(ct);
        await using ServiceProvider laptop = await LaptopAsync(ct);
        await SubmitAsync(await GrantAsync(ct), Feature, ct);

        // Act
        await ProcessAsync(laptop, ct);

        // Assert
        (await CheckoutAsync(ct)).Should().BeEquivalentTo(before);
    }

    // Spec 59 AC-6: Central does not hold the project, so grant and submit send the job; the node's report carries git's own message.
    [Fact]
    public async Task Run_BaseAbsentOnOrigin_FailedGitErrorBase_WithGitsMessage_ModelNeverStarted()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using ServiceProvider laptop = await LaptopAsync(ct);
        await SubmitAsync(await GrantAsync(ct), Feature, ct);

        // Act
        await ProcessAsync(laptop, ct);

        // Assert
        Model report = await SingleReportAsync(ct);
        new
        {
            report.Report!.Status,
            report.Report.Reason,
            report.BodyText,
            Started = File.Exists(CapturePath),
        }.Should().BeEquivalentTo(new { Status = ReportStatus.Failed, Reason = "git_error", BodyText = "git_error: base: origin/feature/shop not found (fatal: Needed a single revision)\n", Started = false });
    }

    [Fact]
    public async Task Run_BaseIsTheDefaultBranch_DefaultBranchNotPushed_ItsBranchPushed_ReportSaysSo()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string? main = await _project.ResolveAsync(_project.OriginDir, "refs/heads/main", ct);
        await using ServiceProvider laptop = await LaptopAsync(ct);
        string job = await SubmitAsync(await GrantAsync(ct, "main"), "main", ct);

        // Act
        await ProcessAsync(laptop, ct);

        // Assert
        Model report = await SingleReportAsync(ct);
        new
        {
            report.Report!.Status,
            Main = await _project.ResolveAsync(_project.OriginDir, "refs/heads/main", ct),
            Pushed = await _project.ResolveAsync(_project.OriginDir, "refs/heads/zyggy/" + job, ct) is not null,
            SaysSo = report.BodyText.Contains("branch main not updated: default branch", StringComparison.Ordinal),
        }.Should().BeEquivalentTo(new { Status = ReportStatus.Done, Main = main, Pushed = true, SaysSo = true });
    }

    private async Task<object> CheckoutAsync(CancellationToken ct) => new
    {
        Head = await _project.GitAsync(["rev-parse", "HEAD"], ct),
        Branch = await _project.GitAsync(["symbolic-ref", "HEAD"], ct),
        Status = await _project.GitAsync(["status", "--porcelain=v1", "--untracked-files=all"], ct),
        Readme = await File.ReadAllTextAsync(Path.Combine(_project.Path, "README.md"), ct),
        Config = await File.ReadAllTextAsync(Path.Combine(_project.Path, ".git", "config"), ct),
    };

    private async Task<ServiceProvider> LaptopAsync(CancellationToken ct)
    {
        var environment = new Dictionary<string, string>
        {
            ["ZYGGY_FAKE_CLAUDE_SCENARIO"] = "done",
            ["ZYGGY_FAKE_CLAUDE_CAPTURE"] = CapturePath,
            ["ZYGGY_FAKE_CLAUDE_EDITS"] = "+src/new.txt\tnew",
        };
        NodeConfiguration configuration = TestHost.NodeConfigurationFor(_harness.Bus, CliHarness.HomeLaptop);
        ServiceProvider services = TestHost.BuildRunnerListener(_harness.Bus, CliHarness.HomeLaptop, _harness.Store, FakeClaude.ExecutablePath, configuration, o =>
        {
            o.NodeDir = _project.NodeDir;
            o.TimeoutOverride = TimeSpan.FromSeconds(30);
            o.ModelEnvironment = environment;
        });
        services.GetRequiredService<RegistrySnapshot>().Replace(new RegistryDocument(
            CliHarness.Acme,
            CliHarness.HomeLaptop,
            RegistryOs.Windows,
            DateTimeOffset.UtcNow,
            null,
            null,
            [new RegistryProject(Project, _project.Path.Replace('\\', '/'), "main", [], [], null)]));
        (await services.GetRequiredService<BusListener>().StartAsync(ct)).Should().Be(BusStartupResult.Clean);
        return services;
    }

    private static async Task ProcessAsync(ServiceProvider laptop, CancellationToken ct)
    {
        (await laptop.GetRequiredService<IBusProvider>().PullAsync(ct)).Succeeded.Should().BeTrue();
        (await laptop.GetRequiredService<BusListener>().ProcessOnceAsync(ct)).Should().Be(1);
    }

    private async Task<string> GrantAsync(CancellationToken ct, string branch = Feature)
    {
        string[] args =
        [
            "mandate", "grant", "--to", "home-laptop", "--project", Project, "--goal", "Build the shop", "--spec", "_specs/12-shop.md", "--spec-commit", "0123456789abcdef0123456789abcdef01234567",
            "--branch", branch, "--allowed-tools", "Read,Edit", "--max-jobs", "5", "--expires", "3d",
        ];
        var (exit, console) = await _harness.RunAsync(args, _harness.CentralNodeJson, ct: ct);
        exit.Should().Be(0, console.Stderr);
        return console.Stdout.Split(' ', 3)[1];
    }

    private async Task<string> SubmitAsync(string mandate, string baseBranch, CancellationToken ct)
    {
        var (exit, console) = await _harness.RunAsync(
            ["submit", "--to", "home-laptop", "--mandate", mandate, "--kind", "build", "--step", "slice 1", "--base", baseBranch],
            _harness.CentralNodeJson,
            ct: ct);
        exit.Should().Be(0, console.Stderr);
        return console.Stdout.Split(' ', StringSplitOptions.RemoveEmptyEntries).Single(w => EnvelopeId.TryParse(w.Trim(), out _)).Trim();
    }

    private async Task<Model> SingleReportAsync(CancellationToken ct)
    {
        string listing = await _harness.Bus.RunGitAsync(_harness.Bus.BareDir, ["ls-tree", "-r", "--name-only", "main", "--", LaptopReports], ct);
        string path = listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.EndsWith(".md", StringComparison.Ordinal) && string.Equals(p[..p.LastIndexOf('/')], LaptopReports, StringComparison.Ordinal))
            .Should().ContainSingle().Subject;
        return await _harness.ParseOnMainAsync(path, ct);
    }
}
