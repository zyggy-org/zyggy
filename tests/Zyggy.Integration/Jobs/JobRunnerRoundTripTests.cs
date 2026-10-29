using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Extensions.DependencyInjection;

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
/// Spec 06 AC-17, AC-18, AC-22..AC-24: one <see cref="BusListener.ProcessOnceAsync"/> of the real listener with the job runner as its
/// handler, the real <c>git</c> on the bare repository and the fake <c>claude</c>.
/// </summary>
public sealed partial class JobRunnerRoundTripTests : IAsyncLifetime
{
    private const string BodyToken = "body-token-5d2c";

    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName Central = MachineName.Parse("central");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly string[] AllowedTools = ["Read", "Bash(dotnet *)"];

    private readonly BusRepoFixture _bus = new(Acme);

    private readonly ScratchDirectory _scratch = new();

    private readonly HookedProcessRunner _hooks = TestHost.Hooks();

    private readonly RecordingLoggerProvider _log = new();

    private InMemorySecretStore _store = null!;

    private ScratchProject _project = null!;

    public static TheoryData<string> Cases => new() { "flipped", "to-central", "schema2", "no-front-matter" };

    private string NodeDir => Path.Combine(_scratch.Path, "node");

    private string CapturePath => _scratch.File("capture.bin");

    private string StdinPath => _scratch.File("stdin.bin");

    public async ValueTask InitializeAsync()
    {
        await _bus.InitializeAsync();
        await _bus.SeedTenantAsync(Acme, [Central, HomeLaptop], CancellationToken.None);
        _store = await TestHost.StoreAsync(Acme, CancellationToken.None);
        _project = await ScratchProject.CreateAsync(_bus, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        _project?.Dispose();
        _scratch.Dispose();
        await _bus.DisposeAsync();
    }

    [Fact]
    public async Task ProcessOnceAsync_DoneScenario_FakeClaudeRanWithZyggyHooksOff()
    {
        // Arrange: spec 12 D5 — the real listener launches the fake claude with the hooks off
        var ct = TestContext.Current.CancellationToken;
        await using ServiceProvider services = TestHost.BuildRunnerListener(_bus, HomeLaptop, _store, FakeClaude.ExecutablePath, Configuration(), Runner("done"), _hooks);
        await SeedAndPullAsync(services, Fields(_project.Path), ct);

        // Act
        (await services.GetRequiredService<BusListener>().ProcessOnceAsync(ct)).Should().Be(1);

        // Assert
        _hooks.Calls.Single(c => c.FileName == FakeClaude.ExecutablePath).Environment.Should().Contain("ZYGGY_HOOKS", "off");
    }

    [Fact]
    public async Task ProcessOnceAsync_DoneScenario_VerifiedDoneReportWithMetricsOnMain()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using ServiceProvider services = TestHost.BuildRunnerListener(_bus, HomeLaptop, _store, FakeClaude.ExecutablePath, Configuration(), Runner("done"));
        SeededJob job = await SeedAndPullAsync(services, Fields(_project.Path), ct);

        // Act
        (await services.GetRequiredService<BusListener>().ProcessOnceAsync(ct)).Should().Be(1);

        // Assert
        Model report = await SingleReportAsync(ct);
        report.Header.InReplyTo.Should().Be(job.Id);
        report.Report!.Status.Should().Be(ReportStatus.Done);
        report.Report.Reason.Should().BeNull();
        report.Report.DurationSeconds.Should().NotBeNull();
        report.Report.Started.Should().NotBeNull();
        report.Report.Finished.Should().NotBeNull();
        report.Report.FilesChanged.Should().NotBeNull().And.BeEmpty();
        report.Report.DiffRef.Should().BeNull();
        report.BodyText.Should().Be(ExpectedReportSection(await ScenarioResultTextAsync("done", ct)) + "\n");
        (await _bus.ExistsOnMainAsync(BusPaths.EnvelopeFile(BusPaths.Claimed(Acme, HomeLaptop), job.Id), ct)).Should().BeFalse();
        (await _bus.LastCommitSubjectAsync(ct)).Should().Be($"acme report {report.Header.Id.Value} home-laptop→central");
        (await File.ReadAllBytesAsync(TranscriptPaths.For(NodeDir, Acme, job.Id), ct))
            .Should().Equal(await File.ReadAllBytesAsync(Path.Combine(FakeClaude.ScenarioDirectory, "done.jsonl"), ct));
        ProjectLock.IsHeld(_project.Path).Should().BeFalse();
        File.Exists(ProjectLock.PathOf(_project.Path)).Should().BeFalse();

        // The run's metrics: present because done.jsonl is the real capture (total_cost_usd, usage) — plan 06 note 3.
        report.Report.CostUsd.Should().NotBeNull();
        report.Report.NumTurns.Should().NotBeNull();
        report.Report.InputTokens.Should().NotBeNull();
        report.Report.OutputTokens.Should().NotBeNull();
        report.Report.Model.Should().NotBeNull();
    }

    [Fact]
    public async Task ProcessOnceAsync_NoReportScenario_DoneWithLastTextTruncated()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using ServiceProvider services = TestHost.BuildRunnerListener(_bus, HomeLaptop, _store, FakeClaude.ExecutablePath, Configuration(), Runner("no-report"));
        await SeedAndPullAsync(services, Fields(_project.Path), ct);
        string text = await ScenarioResultTextAsync("no-report", ct);

        // Act
        (await services.GetRequiredService<BusListener>().ProcessOnceAsync(ct)).Should().Be(1);

        // Assert
        Model report = await SingleReportAsync(ct);
        report.Report!.Status.Should().Be(ReportStatus.Done);
        report.BodyText.Should().Be(text[..1999] + "…\n");
    }

    [Fact]
    public async Task ProcessOnceAsync_ProjectWithoutClaudeDirectory_RejectedUnknownProjectFakeNeverStarted()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using ScratchProject bare = await ScratchProject.WithoutClaudeAsync(_bus, ct);
        await using ServiceProvider services = TestHost.BuildRunnerListener(_bus, HomeLaptop, _store, FakeClaude.ExecutablePath, Configuration(), Runner("done"));
        await SeedAndPullAsync(services, Fields(bare.Path), ct);

        // Act
        (await services.GetRequiredService<BusListener>().ProcessOnceAsync(ct)).Should().Be(1);

        // Assert
        Model report = await SingleReportAsync(ct);
        report.Report!.Status.Should().Be(ReportStatus.Rejected);
        report.Report.Reason.Should().Be("unknown_project");
        File.Exists(CapturePath).Should().BeFalse("the fake never started");
    }

    [Fact]
    public async Task ProcessOnceAsync_LockHeldByTheTest_FailedLockedFakeNeverStarted()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using ServiceProvider services = TestHost.BuildRunnerListener(_bus, HomeLaptop, _store, FakeClaude.ExecutablePath, Configuration(), Runner("done"));
        await SeedAndPullAsync(services, Fields(_project.Path), ct);
        using ProjectLock? held = ProjectLock.TryAcquire(_project.Path, EnvelopeId.New(DateTimeOffset.UtcNow), ProjectLock.CurrentProcessId, DateTimeOffset.UtcNow);
        held.Should().NotBeNull();

        // Act
        (await services.GetRequiredService<BusListener>().ProcessOnceAsync(ct)).Should().Be(1);

        // Assert
        Model report = await SingleReportAsync(ct);
        report.Report!.Status.Should().Be(ReportStatus.Failed);
        report.Report.Reason.Should().Be("locked");
        File.Exists(CapturePath).Should().BeFalse("the fake never started");
    }

    [Fact]
    public async Task ProcessOnceAsync_DoneScenario_CapturedArgvStdinAndCwdMatchTheContract()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using ServiceProvider services = TestHost.BuildRunnerListener(_bus, HomeLaptop, _store, FakeClaude.ExecutablePath, Configuration(), Runner("done"));
        SeededJob seeded = await SeedAndPullAsync(services, Fields(_project.Path), ct);
        EnvelopeResult parsed = await new EnvelopeSigner(_store).ParseAndVerifyAsync(seeded.Bytes, Acme, ct);
        Model job = parsed.Envelope!;

        // Act
        (await services.GetRequiredService<BusListener>().ProcessOnceAsync(ct)).Should().Be(1);

        // Assert
        FakeClaudeCapture capture = FakeClaude.ReadCapture(CapturePath);
        capture.Arguments.Should().Equal(
            "-p", "--output-format", "stream-json", "--verbose", "--permission-mode", "auto", "--permission-prompts", "none",
            "--no-session-persistence", "--max-turns", "60", "--allowedTools", "Read,Bash(dotnet *)");
        Path.GetFullPath(capture.WorkingDirectory).TrimEnd(Path.DirectorySeparatorChar)
            .Should().Be(Path.GetFullPath(_project.Path).TrimEnd(Path.DirectorySeparatorChar));
        (await File.ReadAllBytesAsync(StdinPath, ct)).Should().Equal(Encoding.UTF8.GetBytes(new JobPrompt().Render(job, _project.Path, AllowedTools)));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ProcessOnceAsync_IntakeRejections_RejectedBeforeAnyModelCall(string variant)
    {
        // Arrange: 04's four rejection cases, with the job runner as the handler.
        var ct = TestContext.Current.CancellationToken;
        await using ServiceProvider services = TestHost.BuildRunnerListener(_bus, HomeLaptop, _store, FakeClaude.ExecutablePath, Configuration(), Runner("done"));
        BusListener listener = services.GetRequiredService<BusListener>();
        (await listener.StartAsync(ct)).Should().Be(BusStartupResult.Clean);
        JobFields fields = Fields(_project.Path);
        string path;
        bool reportExpected = true;
        switch (variant)
        {
            case "flipped":
                path = (await _bus.SeedSignedJobAsync(Central, HomeLaptop, TestKeys.Acme1, _store, Body, mutate: b => { b[^2] ^= 0x01; return b; }, fields: fields, ct: ct)).RelativePath;
                break;
            case "to-central":
                var id = EnvelopeId.New(DateTimeOffset.UtcNow);
                path = BusPaths.EnvelopeFile(BusPaths.Jobs(Acme, HomeLaptop), id);
                await _bus.SeedRawFileAsync(path, await TestEnvelopes.SignedJobBytesAsync(Central, Central, _store, TestKeys.Acme1, Body, ct, id: id), "misfiled", ct);
                break;
            case "schema2":
                path = (await _bus.SeedSignedJobAsync(Central, HomeLaptop, TestKeys.Acme1, _store, Body, mutate: b => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(b).Replace("schema: 1", "schema: 2", StringComparison.Ordinal)), fields: fields, ct: ct)).RelativePath;
                break;
            default:
                path = BusPaths.Jobs(Acme, HomeLaptop) + "/weird.md";
                await _bus.SeedRawFileAsync(path, Encoding.UTF8.GetBytes("not an envelope\n"), "raw", ct);
                reportExpected = false;
                break;
        }

        (await services.GetRequiredService<IBusProvider>().PullAsync(ct)).Succeeded.Should().BeTrue();

        // Act
        (await listener.ProcessOnceAsync(ct)).Should().Be(1);

        // Assert
        (await _bus.ExistsOnMainAsync(path, ct)).Should().BeFalse();
        (await _bus.RunGitAsync(_bus.BareDir, ["ls-tree", "--name-only", "main", BusPaths.Rejected(Acme, HomeLaptop) + "/"], ct))
            .Split('\n').Should().Contain(p => p.EndsWith(".md", StringComparison.Ordinal));
        IReadOnlyList<string> reports = await ReportPathsAsync(ct);
        reports.Should().HaveCount(reportExpected ? 1 : 0);
        if (reportExpected)
        {
            (await ParseReportAsync(reports[0], ct)).Report!.Status.Should().Be(ReportStatus.Rejected);
        }

        File.Exists(CapturePath).Should().BeFalse("an intake rejection never reaches the model");
    }

    [Fact]
    public async Task ProcessOnceAsync_DoneScenario_LogsRunPhaseOncePerStateChange()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using ServiceProvider services = TestHost.BuildRunnerListener(
            _bus, HomeLaptop, _store, FakeClaude.ExecutablePath, Configuration(), Runner("done"), _hooks, _log);
        SeededJob job = await SeedAndPullAsync(services, Fields(_project.Path), ct);

        // Act
        (await services.GetRequiredService<BusListener>().ProcessOnceAsync(ct)).Should().Be(1);

        // Assert
        var runner = _log.Entries.Where(e => e.Category.EndsWith("JobRunner", StringComparison.Ordinal)).ToList();
        runner.Where(e => e.Message.Contains(": started ", StringComparison.Ordinal)).Should().ContainSingle();
        runner.Where(e => e.Message.Contains(": finished ", StringComparison.Ordinal)).Should().ContainSingle();
        runner.Should().AllSatisfy(e =>
        {
            e.Scope.Should().NotBeNull();
            e.Scope!.Should().ContainKey("phase").WhoseValue.Should().Be("run");
            e.Scope.Should().ContainKey("jobId").WhoseValue.Should().Be(job.Id.Value);
        });
        SecretSweep.AssertAbsent(_hooks.Calls.SelectMany(SecretSweep.TextsOf));
        SecretSweep.AssertAbsent(_log.Messages);
        _log.Messages.Should().NotContain(m => m.Contains(BodyToken, StringComparison.Ordinal));
    }

    private static string Body => "Read README.md and finish with a REPORT section; change nothing. " + BodyToken + "\n";

    private static JobFields Fields(string project) => new(project, TimeoutMinutes: 2, AllowedTools: AllowedTools);

    // The REPORT section the way the spec defines it: from the first heading line REPORT (any Markdown level), else the whole text.
    private static string ExpectedReportSection(string resultText)
    {
        string text = resultText.Replace("\r", string.Empty, StringComparison.Ordinal);
        Match heading = ReportHeading().Match(text);
        return heading.Success ? text[heading.Index..] : text;
    }

    private static async Task<string> ScenarioResultTextAsync(string scenario, CancellationToken ct)
    {
        string[] lines = (await File.ReadAllTextAsync(Path.Combine(FakeClaude.ScenarioDirectory, scenario + ".jsonl"), ct))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using JsonDocument last = JsonDocument.Parse(lines[^1]);
        return last.RootElement.GetProperty("result").GetString()!;
    }

    [GeneratedRegex(@"^(#+[ \t]*)?REPORT[ \t]*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ReportHeading();

    private NodeConfiguration Configuration() => TestHost.NodeConfigurationFor(_bus, HomeLaptop);

    private Action<JobRunnerOptions> Runner(string scenario) => o =>
    {
        o.NodeDir = NodeDir;
        o.TimeoutOverride = TimeSpan.FromSeconds(30);
        o.ModelEnvironment = new Dictionary<string, string>
        {
            ["ZYGGY_FAKE_CLAUDE_SCENARIO"] = scenario,
            ["ZYGGY_FAKE_CLAUDE_CAPTURE"] = CapturePath,
            ["ZYGGY_FAKE_CLAUDE_STDIN_CAPTURE"] = StdinPath,
        };
    };

    private async Task<SeededJob> SeedAndPullAsync(ServiceProvider services, JobFields fields, CancellationToken ct)
    {
        (await services.GetRequiredService<BusListener>().StartAsync(ct)).Should().Be(BusStartupResult.Clean);
        SeededJob job = await _bus.SeedSignedJobAsync(Central, HomeLaptop, TestKeys.Acme1, _store, Body, fields: fields, ct: ct);
        (await services.GetRequiredService<IBusProvider>().PullAsync(ct)).Succeeded.Should().BeTrue();
        return job;
    }

    private async Task<Model> SingleReportAsync(CancellationToken ct)
    {
        string path = (await ReportPathsAsync(ct)).Should().ContainSingle().Subject;
        return await ParseReportAsync(path, ct);
    }

    private async Task<Model> ParseReportAsync(string path, CancellationToken ct)
    {
        EnvelopeResult parsed = await new EnvelopeSigner(_store).ParseAndVerifyAsync(await _bus.ShowBytesAsync(path, ct), Acme, ct);
        parsed.IsAccepted.Should().BeTrue(parsed.Rejection?.ToString());
        return parsed.Envelope!;
    }

    private async Task<IReadOnlyList<string>> ReportPathsAsync(CancellationToken ct)
    {
        string output = await _bus.RunGitAsync(_bus.BareDir, ["ls-tree", "--name-only", "main", BusPaths.Reports(Acme, HomeLaptop) + "/"], ct);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(p => p.EndsWith(".md", StringComparison.Ordinal)).ToList();
    }
}
