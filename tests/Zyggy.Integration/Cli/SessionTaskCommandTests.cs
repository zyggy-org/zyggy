using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Registry;
using Zyggy.Core.Tests.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Cli;

/// <summary>
/// Spec 18 slice 14 through the real verbs over a bare repository: Central submits a session task under a mandate with
/// <c>sessions: true</c>; at <c>home-laptop</c>'s next turn end <c>zyggy work --turn-end</c> claims it and answers the Stop hook with the
/// fixed frame, so the session goes on with the task; <c>zyggy work --done</c> or <c>--failed</c> writes the report and removes the claim
/// (AC-31); a session task nobody picks up is removed by Central's sweep with a <c>failed</c> / <c>no_session</c> report (AC-32, AC-2).
/// </summary>
public sealed class SessionTaskCommandTests : IAsyncLifetime
{
    private const string Project = "shop";

    private const string Marker = "CANARY-81c4-never-in-the-report";

    private static readonly DateTimeOffset T = new(2026, 11, 21, 10, 0, 0, TimeSpan.Zero);

    private static readonly string[] DoneHead = ["status: done", "files_changed: 2"];

    private static readonly string[] FixedKeys = ["status", "files_changed", "head"];

    private readonly FakeTimeProvider _clock = new(T);

    private CliHarness _harness = null!;

    private string _session = null!;

    private static string LaptopJobs => BusPaths.Jobs(CliHarness.Acme, CliHarness.HomeLaptop);

    private static string LaptopClaimed => BusPaths.Claimed(CliHarness.Acme, CliHarness.HomeLaptop);

    private static string LaptopReports => BusPaths.Reports(CliHarness.Acme, CliHarness.HomeLaptop);

    private static string CentralReports => BusPaths.Reports(CliHarness.Acme, CliHarness.Central);

    public async ValueTask InitializeAsync()
    {
        CancellationToken ct = CancellationToken.None;
        _harness = await CliHarness.CreateAsync(ct);
        var registry = new RegistryDocument(CliHarness.Acme, CliHarness.HomeLaptop, RegistryOs.Windows, T.AddMinutes(-1), null, null, [])
        {
            Capabilities = [RegistryDocument.SupervisionCapability],
        };
        await _harness.Bus.SeedRawFileAsync(BusPaths.Registry(CliHarness.Acme, CliHarness.HomeLaptop), registry.Serialize(), "seed: home-laptop registry", ct);

        // The live session's project: a git repository in a folder named like the project, one committed file.
        _session = _harness.Scratch.File(Project);
        Directory.CreateDirectory(_session);
        await _harness.Bus.RunGitAsync(_session, ["init", "-q", "-b", "main"], ct);
        await File.WriteAllTextAsync(Path.Combine(_session, "a.txt"), "a\n", ct);
        await _harness.Bus.RunGitAsync(_session, ["add", "a.txt"], ct);
        await _harness.Bus.RunGitAsync(_session, ["-c", "user.name=t", "-c", "user.email=t@example.org", "commit", "-q", "-m", "seed"], ct);
    }

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    // AC-31: a turn end claims the task and answers the Stop hook with the frame and the rendered template.
    [Fact]
    public async Task TurnEnd_SessionTaskUnderAMandateWithSessions_ClaimedAndFrameOnStdout()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string mandate = await GrantAsync(sessions: true, ct);
        string job = await SubmitAsync(mandate, ct);

        // Act
        var (exit, console) = await TurnEndAsync(ct);

        // Assert
        using JsonDocument answer = JsonDocument.Parse(console.Stdout);
        new
        {
            Exit = exit,
            Decision = answer.RootElement.GetProperty("decision").GetString(),
            Frame = answer.RootElement.GetProperty("reason").GetString()!.StartsWith("Zyggy task " + job + " (mandate " + mandate + ", step slice 1)", StringComparison.Ordinal),
            Claimed = await FilesOnMainAsync(LaptopClaimed, ct),
            Queued = await FilesOnMainAsync(LaptopJobs, ct),
        }.Should().BeEquivalentTo(new
        {
            Exit = 0,
            Decision = "block",
            Frame = true,
            Claimed = new[] { LaptopClaimed + "/" + job + ".md" },
            Queued = Array.Empty<string>(),
        }, console.Stdout + console.Stderr);
    }

    // AC-31: without sessions: true in the mandate nothing is delivered (the job is signed by hand: the submit itself refuses it).
    [Fact]
    public async Task TurnEnd_MandateWithoutSessions_NothingDelivered()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string mandate = await GrantAsync(sessions: false, ct);
        await _harness.Bus.SeedSignedJobAsync(
            CliHarness.Central,
            CliHarness.HomeLaptop,
            TestKeys.Acme1,
            _harness.Store,
            "Build slice 1.\n",
            fields: new JobFields(Project, AllowedTools: ["Read"], TimeoutMinutes: 60, Mandate: EnvelopeId.Parse(mandate), Step: "slice 1", Deliver: JobDelivery.Session),
            ct: ct);

        // Act
        var (exit, console) = await TurnEndAsync(ct);

        // Assert
        new { Exit = exit, Out = console.Stdout, Claimed = await FilesOnMainAsync(LaptopClaimed, ct) }
            .Should().BeEquivalentTo(new { Exit = 0, Out = string.Empty, Claimed = Array.Empty<string>() }, console.Stderr);
    }

    // AC-31: one task at a time — while a task is open, the next turn end delivers nothing.
    [Fact]
    public async Task TurnEnd_TaskOpen_SecondTaskNotDelivered()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string mandate = await GrantAsync(sessions: true, ct);
        await SubmitAsync(mandate, ct);
        (await TurnEndAsync(ct)).Exit.Should().Be(0);
        await SubmitAsync(mandate, ct, step: "slice 2");

        // Act
        var (exit, console) = await TurnEndAsync(ct);

        // Assert
        new { Exit = exit, Out = console.Stdout, Claimed = (await FilesOnMainAsync(LaptopClaimed, ct)).Count, Queued = (await FilesOnMainAsync(LaptopJobs, ct)).Count }
            .Should().BeEquivalentTo(new { Exit = 0, Out = string.Empty, Claimed = 1, Queued = 1 }, console.Stderr);
    }

    // AC-31: a turn end that is itself a continuation (stop_hook_active) never takes a task.
    [Fact]
    public async Task TurnEnd_StopHookActive_NothingDelivered()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string mandate = await GrantAsync(sessions: true, ct);
        await SubmitAsync(mandate, ct);

        // Act
        var (exit, console) = await TurnEndAsync(ct, active: true);

        // Assert
        new { Exit = exit, Out = console.Stdout, Claimed = await FilesOnMainAsync(LaptopClaimed, ct) }
            .Should().BeEquivalentTo(new { Exit = 0, Out = string.Empty, Claimed = Array.Empty<string>() }, console.Stderr);
    }

    // "Stop": after `zyggy supervise stop` on the laptop no session task is delivered.
    [Fact]
    public async Task TurnEnd_AfterSuperviseStop_NothingDelivered()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string mandate = await GrantAsync(sessions: true, ct);
        await SubmitAsync(mandate, ct);
        (await _harness.RunAsync(["supervise", "stop"], _harness.HomeLaptopNodeJson, ct: ct)).Exit.Should().Be(0);

        // Act
        var (exit, console) = await TurnEndAsync(ct);

        // Assert
        new { Exit = exit, Out = console.Stdout, Claimed = await FilesOnMainAsync(LaptopClaimed, ct) }
            .Should().BeEquivalentTo(new { Exit = 0, Out = string.Empty, Claimed = Array.Empty<string>() }, console.Stderr);
    }

    // AC-31: --done writes the report with mandate and step, fixed fields and the why-less body, and removes the claim.
    [Fact]
    public async Task Done_AfterDelivery_ReportWrittenClaimRemoved()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string mandate = await GrantAsync(sessions: true, ct);
        string job = await SubmitAsync(mandate, ct);
        (await TurnEndAsync(ct)).Exit.Should().Be(0);
        await File.WriteAllTextAsync(Path.Combine(_session, "a.txt"), "a changed\n", ct);
        await File.WriteAllTextAsync(Path.Combine(_session, "b.txt"), "b\n", ct);

        // Act
        var (exit, console) = await _harness.RunAsync(["work", "--done", job], _harness.HomeLaptopNodeJson, more: Clock, ct: ct);

        // Assert
        Model report = await _harness.ParseOnMainAsync((await FilesOnMainAsync(LaptopReports, ct)).Should().ContainSingle(console.Stderr).Subject, ct);
        new
        {
            Exit = exit,
            report.Report!.Status,
            report.Report.Mandate,
            report.Report.Step,
            report.Header.InReplyTo,
            Body = report.BodyText.Split('\n')[0..2],
            Claimed = await FilesOnMainAsync(LaptopClaimed, ct),
        }.Should().BeEquivalentTo(new
        {
            Exit = 0,
            Status = ReportStatus.Done,
            Mandate = (EnvelopeId?)EnvelopeId.Parse(mandate),
            Step = "slice 1",
            InReplyTo = (EnvelopeId?)EnvelopeId.Parse(job),
            Body = DoneHead,
            Claimed = Array.Empty<string>(),
        }, console.Stderr);
    }

    // AC-31: --failed carries the why line on a machine that is not a work machine.
    [Fact]
    public async Task Failed_WithWhy_FailedReportCarriesTheLine()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string mandate = await GrantAsync(sessions: true, ct);
        string job = await SubmitAsync(mandate, ct);
        (await TurnEndAsync(ct)).Exit.Should().Be(0);

        // Act
        var (exit, console) = await _harness.RunAsync(["work", "--failed", job, "--why", "the build is red"], _harness.HomeLaptopNodeJson, more: Clock, ct: ct);

        // Assert
        Model report = await _harness.ParseOnMainAsync((await FilesOnMainAsync(LaptopReports, ct)).Should().ContainSingle(console.Stderr).Subject, ct);
        new { Exit = exit, report.Report!.Status, Why = report.BodyText.EndsWith("why: the build is red\n", StringComparison.Ordinal) }
            .Should().BeEquivalentTo(new { Exit = 0, Status = ReportStatus.Failed, Why = true }, report.BodyText);
    }

    // AC-32: on a work machine (supervision.detail fixed) the body holds only the fixed fields; the why line stays out.
    [Fact]
    public async Task Failed_OnAWorkMachine_BodyHoldsOnlyTheFixedFields()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        JsonNode json = JsonNode.Parse(await File.ReadAllTextAsync(_harness.HomeLaptopNodeJson, ct))!;
        json["supervision"] = new JsonObject { ["detail"] = "fixed" };
        await File.WriteAllTextAsync(_harness.HomeLaptopNodeJson, json.ToJsonString(), ct);
        string mandate = await GrantAsync(sessions: true, ct);
        string job = await SubmitAsync(mandate, ct);
        (await TurnEndAsync(ct)).Exit.Should().Be(0);

        // Act
        var (exit, console) = await _harness.RunAsync(["work", "--failed", job, "--why", Marker], _harness.HomeLaptopNodeJson, more: Clock, ct: ct);

        // Assert
        Model report = await _harness.ParseOnMainAsync((await FilesOnMainAsync(LaptopReports, ct)).Should().ContainSingle(console.Stderr).Subject, ct);
        new { Exit = exit, Keys = report.BodyText.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l[..l.IndexOf(':', StringComparison.Ordinal)]).ToArray(), Leak = report.BodyText.Contains(Marker, StringComparison.Ordinal) }
            .Should().BeEquivalentTo(new { Exit = 0, Keys = FixedKeys, Leak = false }, report.BodyText);
    }

    // AC-31: a why line that looks like a secret is refused with exit 5 and nothing is written.
    [Fact]
    public async Task Failed_WhyMatchesASecretPattern_Exit5NothingWritten()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string mandate = await GrantAsync(sessions: true, ct);
        string job = await SubmitAsync(mandate, ct);
        (await TurnEndAsync(ct)).Exit.Should().Be(0);
        int commits = await _harness.CommitCountOnMainAsync(ct);

        // Act
        var (exit, console) = await _harness.RunAsync(["work", "--failed", job, "--why", "token ghp_" + new string('a', 36)], _harness.HomeLaptopNodeJson, more: Clock, ct: ct);

        // Assert
        new { Exit = exit, Commits = await _harness.CommitCountOnMainAsync(ct) }.Should().BeEquivalentTo(new { Exit = 5, Commits = commits }, console.Stderr);
    }

    // AC-32, AC-2: a session task nobody took is removed by Central's sweep after its 60 minutes, with one failed / no_session report.
    [Fact]
    public async Task Sweep_SessionTaskPastItsTimeout_RemovedWithANoSessionReport_VerifyClean()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string mandate = await GrantAsync(sessions: true, ct);
        string job = await SubmitAsync(mandate, ct);
        _clock.Advance(TimeSpan.FromMinutes(61));

        // Act
        var (exit, console) = await _harness.RunAsync(["sweep"], _harness.CentralNodeJson, more: Clock, ct: ct);

        // Assert
        Model report = await _harness.ParseOnMainAsync((await FilesOnMainAsync(CentralReports, ct)).Should().ContainSingle(console.Stdout + console.Stderr).Subject, ct);
        var (verifyExit, verify) = await _harness.RunAsync(["verify"], _harness.CentralNodeJson, ct: ct);
        new
        {
            Exit = exit,
            report.Report!.Status,
            report.Report.Reason,
            report.Report.Mandate,
            report.Report.Step,
            report.Header.InReplyTo,
            Queued = await FilesOnMainAsync(LaptopJobs, ct),
            Subject = await _harness.Bus.LastCommitSubjectAsync(ct),
            Verify = verifyExit,
        }.Should().BeEquivalentTo(new
        {
            Exit = 0,
            Status = ReportStatus.Failed,
            Reason = "no_session",
            Mandate = (EnvelopeId?)EnvelopeId.Parse(mandate),
            Step = "slice 1",
            InReplyTo = (EnvelopeId?)EnvelopeId.Parse(job),
            Queued = Array.Empty<string>(),
            Subject = "acme report " + report.Header.Id.Value + " central→central",
            Verify = 0,
        }, console.Stdout + console.Stderr + verify.Stdout);
    }

    // AC-32: inside its timeout the task stays queued.
    [Fact]
    public async Task Sweep_SessionTaskInsideItsTimeout_LeftQueued()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string mandate = await GrantAsync(sessions: true, ct);
        string job = await SubmitAsync(mandate, ct);
        _clock.Advance(TimeSpan.FromMinutes(59));

        // Act
        var (exit, console) = await _harness.RunAsync(["sweep"], _harness.CentralNodeJson, more: Clock, ct: ct);

        // Assert
        new { Exit = exit, Queued = await FilesOnMainAsync(LaptopJobs, ct), Reports = await FilesOnMainAsync(CentralReports, ct) }
            .Should().BeEquivalentTo(new { Exit = 0, Queued = new[] { LaptopJobs + "/" + job + ".md" }, Reports = Array.Empty<string>() }, console.Stdout + console.Stderr);
    }

    private void Clock(IServiceCollection services) => services.AddSingleton<TimeProvider>(_clock);

    private Task<(int Exit, VerbConsole Console)> TurnEndAsync(CancellationToken ct, bool active = false) =>
        _harness.RunAsync(["work", "--turn-end"], _harness.HomeLaptopNodeJson, stdin: HookJson.Stop(null, active: active, cwd: _session), more: Clock, ct: ct);

    private async Task<string> GrantAsync(bool sessions, CancellationToken ct)
    {
        List<string> args =
        [
            "mandate", "grant", "--to", "home-laptop", "--project", Project, "--goal", "Build the shop", "--spec", "_specs/12-shop.md", "--spec-commit", "0123456789abcdef0123456789abcdef01234567",
            "--allowed-tools", "Read,Edit", "--max-jobs", "5", "--expires", "3d",
        ];
        if (sessions)
        {
            args.Add("--sessions");
        }

        var (exit, console) = await _harness.RunAsync([.. args], _harness.CentralNodeJson, more: Clock, ct: ct);
        exit.Should().Be(0, console.Stderr);
        return console.Stdout.Split(' ', 3)[1].Trim();
    }

    private async Task<string> SubmitAsync(string mandate, CancellationToken ct, string step = "slice 1")
    {
        var (exit, console) = await _harness.RunAsync(["submit", "--to", "home-laptop", "--mandate", mandate, "--kind", "build", "--step", step, "--session"], _harness.CentralNodeJson, more: Clock, ct: ct);
        exit.Should().Be(0, console.Stderr);
        return console.Stdout.Split(' ', StringSplitOptions.RemoveEmptyEntries).Single(w => EnvelopeId.TryParse(w.Trim(), out _)).Trim();
    }

    private async Task<IReadOnlyList<string>> FilesOnMainAsync(string folder, CancellationToken ct)
    {
        string listing = await _harness.Bus.RunGitAsync(_harness.Bus.BareDir, ["ls-tree", "-r", "--name-only", "main", "--", folder], ct);
        return [.. listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.EndsWith(".md", StringComparison.Ordinal) && string.Equals(p[..p.LastIndexOf('/')], folder, StringComparison.Ordinal))];
    }
}
