using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Jobs;
using Zyggy.Core.Runs;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Jobs;

/// <summary>
/// Spec 06 AC-19..AC-21: every way a model run goes wrong, through the real listener, the real <c>git</c> and the fake <c>claude</c>,
/// ends as the right signed report — never as an exception, never with a process left running.
/// </summary>
public sealed class JobRunnerFailureTests : IAsyncLifetime
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName Central = MachineName.Parse("central");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private readonly BusRepoFixture _bus = new(Acme);

    private readonly ScratchDirectory _scratch = new();

    private readonly HookedProcessRunner _hooks = TestHost.Hooks();

    private readonly RecordingLoggerProvider _log = new();

    private InMemorySecretStore _store = null!;

    private ScratchProject _project = null!;

    public static TheoryData<string> FailureCases => new() { "hang", "partial-then-hang", "error-exit-1", "no-result-exit-143", "claude-missing" };

    private string NodeDir => Path.Combine(_scratch.Path, "node");

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
    public async Task ProcessOnceAsync_DelayBeyondOverride_TimeoutReportNoOrphanTranscriptExistsLockReleased()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;

        // Act
        (Model report, SeededJob job) = await RunCaseAsync("hang", ct);

        // Assert
        report.Report!.Status.Should().Be(ReportStatus.Timeout);
        report.Report.Reason.Should().Be("timeout");
        File.Exists(TranscriptPaths.For(NodeDir, Acme, job.Id)).Should().BeTrue();
        ProjectLock.IsHeld(_project.Path).Should().BeFalse();
        _log.AtLevel(LogLevel.Information)
            .Where(e => e.Category.EndsWith("JobRunner", StringComparison.Ordinal) && e.Message.Contains("finished timeout", StringComparison.Ordinal))
            .Should().ContainSingle();
        ProcessProbe.ProcessesWithWorkingDirectory(_project.Path).Should().Be(0);
    }

    [Fact]
    public async Task ProcessOnceAsync_NoResultThenLinger_TimeoutBodyContainsPartialText()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;

        // Act
        (Model report, _) = await RunCaseAsync("partial-then-hang", ct);

        // Assert
        report.Report!.Status.Should().Be(ReportStatus.Timeout);
        report.Report.Reason.Should().Be("timeout");
        report.BodyText.Should().Contain("Looking at the logs…");
        ProjectLock.IsHeld(_project.Path).Should().BeFalse();
        ProcessProbe.ProcessesWithWorkingDirectory(_project.Path).Should().Be(0);
    }

    [Fact]
    public async Task ProcessOnceAsync_ErrorScenarioExitOne_FailedClaudeErrorDuringExecutionWithCost()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;

        // Act
        (Model report, _) = await RunCaseAsync("error-exit-1", ct);

        // Assert
        report.Report!.Status.Should().Be(ReportStatus.Failed);
        report.Report.Reason.Should().Be("claude_error");
        FirstLine(report).Should().Be("claude_error: error_during_execution");
        report.Report.CostUsd.Should().Be(0.001m);
        ProjectLock.IsHeld(_project.Path).Should().BeFalse();
    }

    [Fact]
    public async Task ProcessOnceAsync_NoResultExit143_FailedClaudeErrorNoResult()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;

        // Act
        (Model report, _) = await RunCaseAsync("no-result-exit-143", ct);

        // Assert
        report.Report!.Status.Should().Be(ReportStatus.Failed);
        report.Report.Reason.Should().Be("claude_error");
        FirstLine(report).Should().Be("claude_error: no_result");
        ProjectLock.IsHeld(_project.Path).Should().BeFalse();
    }

    [Fact]
    public async Task ProcessOnceAsync_ClaudePathMissing_FailedClaudeErrorNotFound()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;

        // Act
        (Model report, _) = await RunCaseAsync("claude-missing", ct);

        // Assert
        report.Report!.Status.Should().Be(ReportStatus.Failed);
        report.Report.Reason.Should().Be("claude_error");
        FirstLine(report).Should().Be("claude_error: not_found");
        ProjectLock.IsHeld(_project.Path).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(FailureCases))]
    public async Task ProcessOnceAsync_EveryFailure_ReasonIsOneOfNineWireStrings(string failure)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;

        // Act
        (Model report, SeededJob job) = await RunCaseAsync(failure, ct);

        // Assert
        RunFailureReasonWire.TryFromWire(report.Report!.Reason, out _).Should().BeTrue(report.Report.Reason);
        report.Header.InReplyTo.Should().Be(job.Id);
        (await _bus.ExistsOnMainAsync(BusPaths.EnvelopeFile(BusPaths.Claimed(Acme, HomeLaptop), job.Id), ct)).Should().BeFalse();
        ProjectLock.IsHeld(_project.Path).Should().BeFalse();
        SecretSweep.AssertAbsent(_hooks.Calls.SelectMany(SecretSweep.TextsOf));
        SecretSweep.AssertAbsent(_log.Messages);
    }

    private static string FirstLine(Model report) => report.BodyText.Split('\n')[0];

    // Seeds one job for the scratch project, runs one tick of the listener with the case's fake behaviour, returns the verified report.
    private async Task<(Model Report, SeededJob Job)> RunCaseAsync(string failure, CancellationToken ct)
    {
        var environment = new Dictionary<string, string>();
        string claudePath = FakeClaude.ExecutablePath;
        switch (failure)
        {
            case "hang":
                environment["ZYGGY_FAKE_CLAUDE_DELAY_MS"] = "30000";
                break;
            case "partial-then-hang":
                environment["ZYGGY_FAKE_CLAUDE_SCENARIO"] = "no-result";
                environment["ZYGGY_FAKE_CLAUDE_LINGER_MS"] = "30000";
                break;
            case "error-exit-1":
                environment["ZYGGY_FAKE_CLAUDE_SCENARIO"] = "error";
                environment["ZYGGY_FAKE_CLAUDE_EXIT"] = "1";
                break;
            case "no-result-exit-143":
                environment["ZYGGY_FAKE_CLAUDE_SCENARIO"] = "no-result";
                environment["ZYGGY_FAKE_CLAUDE_EXIT"] = "143";
                break;
            default:
                claudePath = Path.Combine(_scratch.Path, "no-such", "claude");
                break;
        }

        await using ServiceProvider services = TestHost.BuildRunnerListener(
            _bus,
            HomeLaptop,
            _store,
            claudePath,
            TestHost.NodeConfigurationFor(_bus, HomeLaptop),
            o =>
            {
                o.NodeDir = NodeDir;
                o.TimeoutOverride = TimeSpan.FromSeconds(2);
                o.ModelEnvironment = environment;
            },
            _hooks,
            _log);
        BusListener listener = services.GetRequiredService<BusListener>();
        (await listener.StartAsync(ct)).Should().Be(BusStartupResult.Clean);
        SeededJob job = await _bus.SeedSignedJobAsync(
            Central, HomeLaptop, TestKeys.Acme1, _store, "Find out why the env module fails.\n", fields: new JobFields(_project.Path, TimeoutMinutes: 2), ct: ct);
        (await services.GetRequiredService<IBusProvider>().PullAsync(ct)).Succeeded.Should().BeTrue();

        (await listener.ProcessOnceAsync(ct)).Should().Be(1);

        string output = await _bus.RunGitAsync(_bus.BareDir, ["ls-tree", "--name-only", "main", BusPaths.Reports(Acme, HomeLaptop) + "/"], ct);
        string path = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(p => p.EndsWith(".md", StringComparison.Ordinal)).Should().ContainSingle().Subject;
        EnvelopeResult parsed = await new EnvelopeSigner(_store).ParseAndVerifyAsync(await _bus.ShowBytesAsync(path, ct), Acme, ct);
        parsed.IsAccepted.Should().BeTrue(parsed.Rejection?.ToString());
        return (parsed.Envelope!, job);
    }
}
