using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Envelope;
using Zyggy.Core.Jobs;
using Zyggy.Core.Registry;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Cli;
using Zyggy.Integration.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Jobs;

/// <summary>
/// Spec 18 slice 7 over one bare repository: Central grants and submits through the real verbs in its checkout, <c>home-laptop</c>
/// runs the production listener and job runner with <c>tools/fake-claude</c> in a second checkout. A job whose mandate the node
/// cannot match is rejected <c>mandate_invalid</c> before any model call, a matching one runs (AC-16); three failing jobs pause the
/// mandate until <c>zyggy mandate resume</c> (AC-17).
/// </summary>
public sealed class MandateIntakeTests : IAsyncLifetime
{
    private const string Project = "shop";

    private static readonly string[] Tools = ["Read", "Edit"];

    private static readonly Dictionary<string, string?> Unattended = new() { ["ZYGGY_HOOKS"] = "off" };

    private CliHarness _harness = null!;

    private WorktreeProject _project = null!;

    private string CapturePath => _harness.Scratch.File("capture.bin");

    private static string LaptopReports => BusPaths.Reports(CliHarness.Acme, CliHarness.HomeLaptop);

    private static string LaptopRejected => BusPaths.Rejected(CliHarness.Acme, CliHarness.HomeLaptop);

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

    public static TheoryData<string, string> Refusals() => new()
    {
        { "absent", "absent" },
        { "revoked", "revoked" },
        { "expired", "expired" },
        { "machine", "machine" },
        { "project", "project" },
        { "tools", "allowed_tools" },
        { "worktree", "worktree" },
    };

    // AC-16
    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task Intake_OutsideTheMandate_RejectedMandateInvalid_ModelNeverStarted(string fault, string limit)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using ServiceProvider laptop = await LaptopAsync("done", ct);
        string mandate = fault switch
        {
            "absent" => EnvelopeId.New(DateTimeOffset.UtcNow).Value,
            "expired" => await GrantAsync(ct, clock: new FakeTimeProvider(DateTimeOffset.UtcNow.AddDays(-8))),
            "machine" => await GrantAsync(ct, to: "central"),
            _ => await GrantAsync(ct),
        };
        if (fault == "revoked")
        {
            var (revokeExit, revoke) = await _harness.RunAsync(["mandate", "revoke", mandate], _harness.CentralNodeJson, ct: ct);
            revokeExit.Should().Be(0, revoke.Stderr);
        }

        SeededJob job = await SeedJobAsync(
            mandate,
            ct,
            project: fault == "project" ? "other" : Project,
            tools: fault == "tools" ? ["Read", "Bash"] : Tools,
            worktree: fault != "worktree");

        // Act
        await ProcessAsync(laptop, ct);

        // Assert
        Model report = await SingleReportAsync(ct);
        new
        {
            report.Report!.Status,
            report.Report.Reason,
            report.Report.Mandate,
            report.Report.Step,
            report.Header.InReplyTo,
            Body = report.BodyText,
            Rejected = (await FilesOnMainAsync(LaptopRejected, ct)).Count,
            ModelRan = File.Exists(CapturePath),
        }.Should().BeEquivalentTo(new
        {
            Status = ReportStatus.Rejected,
            Reason = "mandate_invalid",
            Mandate = EnvelopeId.Parse(mandate),
            Step = "slice 1",
            InReplyTo = job.Id,
            Body = "rejected: mandate_invalid (mandate)\n",
            Rejected = 1,
            ModelRan = false,
        });
        (await _harness.Bus.LastCommitSubjectAsync(ct)).Should().Be("acme reject " + job.Id.Value + " home-laptop→central", limit);
    }

    // AC-16
    [Fact]
    public async Task Intake_TotalCapReached_RejectedMandateInvalid_ModelNeverStarted()
    {
        // Arrange: the first job uses the mandate's one job; a second one signed by hand comes after it
        var ct = TestContext.Current.CancellationToken;
        await using ServiceProvider laptop = await LaptopAsync("done", ct);
        string mandate = await GrantAsync(ct, maxJobs: "1");
        await SubmitAsync(mandate, "slice 1", ct);
        await ProcessAsync(laptop, ct);
        File.Delete(CapturePath);
        SeededJob second = await SeedJobAsync(mandate, ct, step: "slice 2");

        // Act
        await ProcessAsync(laptop, ct);

        // Assert
        Model report = await ReportInReplyToAsync(second.Id, ct);
        new { report.Report!.Status, report.Report.Reason, ModelRan = File.Exists(CapturePath) }
            .Should().BeEquivalentTo(new { Status = ReportStatus.Rejected, Reason = "mandate_invalid", ModelRan = false });
    }

    // AC-16
    [Fact]
    public async Task Intake_InsideTheMandate_ClaimedAndRunInWorktree_ReportCarriesMandateAndStep()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using ServiceProvider laptop = await LaptopAsync("done", ct);
        string mandate = await GrantAsync(ct);
        string job = await SubmitAsync(mandate, "slice 1", ct);

        // Act
        await ProcessAsync(laptop, ct);

        // Assert
        Model report = await SingleReportAsync(ct);
        new
        {
            report.Report!.Status,
            report.Report.Mandate,
            report.Report.Step,
            report.Header.InReplyTo,
            Ran = FakeClaude.ReadCapture(CapturePath).WorkingDirectory,
        }.Should().BeEquivalentTo(new
        {
            Status = ReportStatus.Done,
            Mandate = EnvelopeId.Parse(mandate),
            Step = "slice 1",
            InReplyTo = EnvelopeId.Parse(job),
            Ran = TranscriptPaths.WorktreeFor(_project.NodeDir, CliHarness.Acme, EnvelopeId.Parse(job)),
        });
    }

    // AC-16
    [Fact]
    public async Task Verify_AfterRunAndRejection_NoDriftOnTheMandateFiles()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using ServiceProvider laptop = await LaptopAsync("done", ct);
        string mandate = await GrantAsync(ct);
        await SubmitAsync(mandate, "slice 1", ct);
        SeededJob refused = await SeedJobAsync(mandate, ct, project: "other", step: "slice 2");
        await ProcessAsync(laptop, ct, expected: 2);
        await _harness.Bus.PullCloneAsync(ct);

        // Act
        var (exit, console) = await _harness.RunAsync(["verify", "--verbose"], _harness.HomeLaptopNodeJson, ct: ct);

        // Assert: the mandate and the job the node refused under it both verify where they now sit
        exit.Should().Be(0, console.Stdout);
        string[] lines = console.Stdout.Split('\n');
        new
        {
            Mandate = lines.Any(l => l.StartsWith("ok", StringComparison.OrdinalIgnoreCase) && l.Contains(BusPaths.Mandates(CliHarness.Acme) + "/" + mandate, StringComparison.Ordinal)),
            Refused = lines.Any(l => l.StartsWith("ok", StringComparison.OrdinalIgnoreCase) && l.Contains(BusPaths.EnvelopeFile(LaptopRejected, refused.Id), StringComparison.Ordinal)),
        }.Should().BeEquivalentTo(new { Mandate = true, Refused = true }, console.Stdout);
    }

    // AC-17
    [Fact]
    public async Task Submit_AfterThreeFailingJobs_RefusedPaused()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using ServiceProvider laptop = await LaptopAsync("error", ct);
        string mandate = await GrantAsync(ct, maxJobs: "10");
        await FailThreeAsync(laptop, mandate, ct);

        // Act
        var (exit, console) = await SubmitRunAsync(mandate, "slice 4", ct);

        // Assert
        exit.Should().Be(3, console.Stdout);
        console.Stderr.Should().Contain("paused");
    }

    // AC-17
    [Fact]
    public async Task Resume_AfterPause_WritesResumeDecision_NextSubmitPasses()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using ServiceProvider laptop = await LaptopAsync("error", ct);
        string mandate = await GrantAsync(ct, maxJobs: "10");
        await FailThreeAsync(laptop, mandate, ct);
        var (paused, _) = await SubmitRunAsync(mandate, "slice 4", ct);
        paused.Should().Be(3, "the mandate is paused before the resume");

        // Act
        var (exit, console) = await _harness.RunAsync(["mandate", "resume", mandate], _harness.CentralNodeJson, ct: ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        string decisionPath = (await FilesOnMainAsync(BusPaths.Decisions(CliHarness.Acme, CliHarness.Central), ct)).Should().ContainSingle().Subject;
        Model decision = await _harness.ParseOnMainAsync(decisionPath, ct);
        new { decision.Decision!.Kind, decision.Header.InReplyTo, decision.Decision.Mandate, decision.Header.To }
            .Should().BeEquivalentTo(new { Kind = DecisionKind.Resume, InReplyTo = (EnvelopeId?)EnvelopeId.Parse(mandate), Mandate = (EnvelopeId?)EnvelopeId.Parse(mandate), To = CliHarness.HomeLaptop });
        var (next, submitted) = await SubmitRunAsync(mandate, "slice 4", ct);
        next.Should().Be(0, submitted.Stderr);
    }

    public static TheoryData<string, int, string> ResumeRefusals() => new()
    {
        { "laptop", 3, "only central" },
        { "unattended", 3, "attended" },
        { "unknown", 1, "no live mandate" },
    };

    [Theory]
    [MemberData(nameof(ResumeRefusals))]
    public async Task Resume_Refused_NothingWritten(string fault, int code, string named)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string mandate = fault == "unknown" ? EnvelopeId.New(DateTimeOffset.UtcNow).Value : await GrantAsync(ct);
        int commits = await _harness.CommitCountOnMainAsync(ct);

        // Act
        var (exit, console) = await _harness.RunAsync(
            ["mandate", "resume", mandate],
            fault == "laptop" ? _harness.HomeLaptopNodeJson : _harness.CentralNodeJson,
            extraEnvironment: fault == "unattended" ? Unattended : null,
            ct: ct);

        // Assert
        exit.Should().Be(code, console.Stdout);
        console.Stderr.Should().Contain(named);
        (await _harness.CommitCountOnMainAsync(ct)).Should().Be(commits);
    }

    [Fact]
    public async Task Decide_ReplyToAMandate_AddressedToTheMandatesMachine()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string mandate = await GrantAsync(ct);

        // Act
        var (exit, console) = await _harness.RunAsync(
            ["decide", "--reply-to", mandate, "--kind", "stop", "--basis", "verified", "--mandate", mandate, "--undo", "grant a new mandate", "Stop the work on the booking pool."],
            _harness.CentralNodeJson,
            ct: ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        string decisionPath = (await FilesOnMainAsync(BusPaths.Decisions(CliHarness.Acme, CliHarness.Central), ct)).Should().ContainSingle().Subject;
        Model decision = await _harness.ParseOnMainAsync(decisionPath, ct);
        new { decision.Header.To, decision.Header.InReplyTo }.Should().BeEquivalentTo(new { To = CliHarness.HomeLaptop, InReplyTo = (EnvelopeId?)EnvelopeId.Parse(mandate) });
    }

    private async Task FailThreeAsync(ServiceProvider laptop, string mandate, CancellationToken ct)
    {
        foreach (string step in (string[])["slice 1", "slice 2", "slice 3"])
        {
            await SubmitAsync(mandate, step, ct);
            await ProcessAsync(laptop, ct);
        }

        foreach (string path in await FilesOnMainAsync(LaptopReports, ct))
        {
            (await _harness.ParseOnMainAsync(path, ct)).Report!.Status.Should().Be(ReportStatus.Failed);
        }
    }

    private async Task<ServiceProvider> LaptopAsync(string scenario, CancellationToken ct)
    {
        var environment = new Dictionary<string, string>
        {
            ["ZYGGY_FAKE_CLAUDE_SCENARIO"] = scenario,
            ["ZYGGY_FAKE_CLAUDE_CAPTURE"] = CapturePath,
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

    private static async Task ProcessAsync(ServiceProvider laptop, CancellationToken ct, int expected = 1)
    {
        (await laptop.GetRequiredService<IBusProvider>().PullAsync(ct)).Succeeded.Should().BeTrue();
        (await laptop.GetRequiredService<BusListener>().ProcessOnceAsync(ct)).Should().Be(expected);
    }

    private Task<SeededJob> SeedJobAsync(string mandate, CancellationToken ct, string project = Project, string[]? tools = null, bool worktree = true, string step = "slice 1") =>
        _harness.Bus.SeedSignedJobAsync(
            CliHarness.Central,
            CliHarness.HomeLaptop,
            TestKeys.Acme1,
            _harness.Store,
            "Build the slice.\n",
            fields: new JobFields(project, Worktree: worktree, TimeoutMinutes: 2, AllowedTools: tools ?? Tools, Mandate: EnvelopeId.Parse(mandate), Step: step),
            ct: ct);

    private async Task<string> GrantAsync(CancellationToken ct, string to = "home-laptop", string maxJobs = "3", FakeTimeProvider? clock = null)
    {
        string[] args =
        [
            "mandate", "grant", "--to", to, "--project", Project, "--goal", "Build the shop", "--spec", "_specs/12-shop.md", "--spec-commit", "0123456789abcdef0123456789abcdef01234567",
            "--allowed-tools", "Read,Edit", "--max-jobs", maxJobs, "--expires", "3d",
        ];
        var (exit, console) = await _harness.RunAsync(args, _harness.CentralNodeJson, more: clock is null ? null : s => s.AddSingleton<TimeProvider>(clock), ct: ct);
        exit.Should().Be(0, console.Stderr);
        return console.Stdout.Split(' ', 3)[1];
    }

    private Task<(int Exit, VerbConsole Console)> SubmitRunAsync(string mandate, string step, CancellationToken ct) =>
        _harness.RunAsync(["submit", "--to", "home-laptop", "--mandate", mandate, "--kind", "build", "--step", step], _harness.CentralNodeJson, ct: ct);

    private async Task<string> SubmitAsync(string mandate, string step, CancellationToken ct)
    {
        var (exit, console) = await SubmitRunAsync(mandate, step, ct);
        exit.Should().Be(0, console.Stderr);
        return console.Stdout.Split(' ', StringSplitOptions.RemoveEmptyEntries).Single(EnvelopeIdShaped);
    }

    private static bool EnvelopeIdShaped(string word) => EnvelopeId.TryParse(word.Trim(), out _);

    private async Task<Model> SingleReportAsync(CancellationToken ct) =>
        await _harness.ParseOnMainAsync((await FilesOnMainAsync(LaptopReports, ct)).Should().ContainSingle().Subject, ct);

    private async Task<Model> ReportInReplyToAsync(EnvelopeId job, CancellationToken ct)
    {
        foreach (string path in await FilesOnMainAsync(LaptopReports, ct))
        {
            Model report = await _harness.ParseOnMainAsync(path, ct);
            if (report.Header.InReplyTo == job)
            {
                return report;
            }
        }

        throw new InvalidOperationException("no report answers " + job.Value);
    }

    private async Task<IReadOnlyList<string>> FilesOnMainAsync(string folder, CancellationToken ct)
    {
        string listing = await _harness.Bus.RunGitAsync(_harness.Bus.BareDir, ["ls-tree", "-r", "--name-only", "main", "--", folder], ct);
        return [.. listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.EndsWith(".md", StringComparison.Ordinal) && string.Equals(p[..p.LastIndexOf('/')], folder, StringComparison.Ordinal))];
    }
}
