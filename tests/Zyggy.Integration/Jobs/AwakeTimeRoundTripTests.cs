using System.Text;

using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Cli;
using Zyggy.Integration.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Jobs;

/// <summary>
/// Spec 61 slice 3 (AC-9): the report of a run that slept says how long. The home laptop's listener with the production job runner and
/// <c>tools/fake-claude</c>, real git and a bare repository; the node host's clock is a <see cref="JumpClock"/> whose wall clock jumps 9
/// hours while the model runs, as a laptop's does across a sleep (its timers stay real). The awake-time arithmetic itself is
/// <c>AwakeTimeTests</c>' subject.
/// </summary>
public sealed class AwakeTimeRoundTripTests : IAsyncLifetime
{
    private readonly HookedProcessRunner _hooks = TestHost.Hooks();

    private readonly JumpClock _clock = new();

    private CliHarness _harness = null!;

    private ScratchProject _project = null!;

    private ServiceProvider _node = null!;

    public async ValueTask InitializeAsync()
    {
        _harness = await CliHarness.CreateAsync(CancellationToken.None);
        _project = await ScratchProject.CreateAsync(_harness.Bus, CancellationToken.None);
        _node = TestHost.BuildRunnerListener(
            _harness.Bus,
            CliHarness.HomeLaptop,
            await TestHost.StoreAsync(CliHarness.Acme, CancellationToken.None),
            FakeClaude.ExecutablePath,
            TestHost.NodeConfigurationFor(_harness.Bus, CliHarness.HomeLaptop),
            o =>
            {
                o.NodeDir = _harness.NodeDirOf("home-laptop-listener");
                o.TimeoutOverride = TimeSpan.FromSeconds(30);
                o.ModelEnvironment = new Dictionary<string, string> { ["ZYGGY_FAKE_CLAUDE_SCENARIO"] = "done" };
            },
            hooks: _hooks,
            extend: s => s.AddSingleton<TimeProvider>(_clock));
    }

    public async ValueTask DisposeAsync()
    {
        await _node.DisposeAsync();
        _project?.Dispose();
        await _harness.DisposeAsync();
    }

    // AC-9: the machine sleeps 9 hours while the model runs; the run is not ended, and its done report carries the asleep line.
    [Fact]
    public async Task Run_MachineSleptNineHours_DoneReportCarriesAsleepLine()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model job = await SeedJobAsync(ct);
        _hooks.BeforeRun = spec =>
        {
            if (spec.FileName == FakeClaude.ExecutablePath)
            {
                _clock.Jump(TimeSpan.FromHours(9));
            }

            return Task.CompletedTask;
        };

        // Act
        (await _node.GetRequiredService<BusListener>().StartAsync(ct)).Should().Be(BusStartupResult.Clean);

        // Assert
        Model report = await ReportAsync(job, ct);
        new { report.Report!.Status, Line = BodyLines(report).Contains("asleep 9h00 during the run") }
            .Should().BeEquivalentTo(new { Status = ReportStatus.Done, Line = true });
    }

    // AC-9: a run that did not sleep carries no asleep line.
    [Fact]
    public async Task Run_MachineAwake_DoneReportWithoutAsleepLine()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model job = await SeedJobAsync(ct);

        // Act
        (await _node.GetRequiredService<BusListener>().StartAsync(ct)).Should().Be(BusStartupResult.Clean);

        // Assert
        Model report = await ReportAsync(job, ct);
        new { report.Report!.Status, Asleep = Encoding.UTF8.GetString(report.Body.Span).Contains("asleep", StringComparison.Ordinal) }
            .Should().BeEquivalentTo(new { Status = ReportStatus.Done, Asleep = false });
    }

    private static string[] BodyLines(Model report) => Encoding.UTF8.GetString(report.Body.Span).Split('\n');

    private async Task<Model> ReportAsync(Model job, CancellationToken ct)
    {
        string output = await _harness.Bus.RunGitAsync(_harness.Bus.BareDir, ["ls-tree", "--name-only", "main", BusPaths.Reports(CliHarness.Acme, CliHarness.HomeLaptop) + "/"], ct);
        string path = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().ContainSingle(p => p.EndsWith(".md", StringComparison.Ordinal)).Subject;
        Model report = await _harness.ParseOnMainAsync(path, ct);
        report.Header.InReplyTo.Should().Be(job.Header.Id);
        return report;
    }

    // A queued job central → home-laptop for the scratch project.
    private async Task<Model> SeedJobAsync(CancellationToken ct)
    {
        Model job = await TestEnvelopes.SignedJobAsync(
            CliHarness.Central,
            CliHarness.HomeLaptop,
            _harness.Store,
            TestKeys.Acme1,
            "Read README.md and finish with a REPORT section; change nothing.\n",
            ct,
            fields: new JobFields(_project.Path, TimeoutMinutes: 2, AllowedTools: ["Read"], Attempt: 1));
        string queued = BusPaths.EnvelopeFile(BusPaths.Jobs(CliHarness.Acme, CliHarness.HomeLaptop), job.Header.Id);
        await _harness.Bus.SeedRawFileAsync(queued, EnvelopeWriter.Serialize(job), "acme job " + job.Header.Id.Value + " central→home-laptop", ct);
        return job;
    }

    /// <summary>The system clock moved by <see cref="Jump"/>: the wall clock jumps, the timers stay real (a sleeping machine's do not run).</summary>
    private sealed class JumpClock : TimeProvider
    {
        private long _offsetTicks;

        public void Jump(TimeSpan span) => Interlocked.Add(ref _offsetTicks, span.Ticks);

        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));
    }
}
