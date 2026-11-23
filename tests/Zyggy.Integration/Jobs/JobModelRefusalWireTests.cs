using System.Text;

using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Envelope;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Cli;
using Zyggy.Integration.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Jobs;

/// <summary>
/// Spec 50 slice 4 (Wire): the refusal paths of a job's model end to end — a signed job on the bare repository, the production node
/// composition running <c>tools/fake-claude</c>, the report read back from <c>main</c>. A model off the node's <c>claude.models</c> is
/// <c>rejected</c> / <c>model_unavailable</c> and Claude Code never starts (AC-3); a <c>system</c> event naming another model stops the
/// real process at once, <c>failed</c> / <c>model_unavailable</c> naming both (AC-4); Claude Code's own refusal, on the captured stream,
/// is <c>model_unavailable</c>, not <c>claude_error</c> (AC-5).
/// </summary>
public sealed class JobModelRefusalWireTests : IAsyncLifetime
{
    // The model fake-claude's "done" scenario names in its system event.
    private const string Fable = "claude-fable-5-1";

    private const string Opus = "claude-opus-5-5";

    private const string JobTask = "Read README.md and finish with a REPORT section.\n";

    private CliHarness _harness = null!;

    private ScratchProject _project = null!;

    private string CapturePath => _harness.Scratch.File("capture.bin");

    private string ScenarioDir => _harness.Scratch.File("scenarios");

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        _harness = await CliHarness.CreateAsync(ct);
        _project = await ScratchProject.CreateAsync(_harness.Bus, ct);
    }

    public async ValueTask DisposeAsync()
    {
        _project?.Dispose();
        await _harness.DisposeAsync();
    }

    // AC-3: node.json claude.models without the job's model: rejected / model_unavailable with the "not allowed" body, and fake-claude
    // never started (no capture file).
    [Fact]
    public async Task ModelRefusal_ModelOffClaudeModels_RejectedModelUnavailableClaudeNeverStarted()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var claude = new ClaudeSettings { Models = [Opus] };

        // Act
        Model report = await RunNodeOnceAsync(Fable, claude, "done", ct: ct);

        // Assert
        new { report.Report!.Status, report.Report.Reason, report.BodyText, Started = File.Exists(CapturePath) }.Should().BeEquivalentTo(new
        {
            Status = ReportStatus.Rejected,
            Reason = "model_unavailable",
            BodyText = "model_unavailable: model " + Fable + " is not allowed on this machine\n",
            Started = false,
        });
    }

    // AC-3: a model on claude.models runs: Claude Code gets --model and the job ends done.
    [Fact]
    public async Task ModelRefusal_ModelOnClaudeModels_RunsDone()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var claude = new ClaudeSettings { Models = [Opus, Fable] };

        // Act
        Model report = await RunNodeOnceAsync(Fable, claude, "done", ct: ct);

        // Assert
        new { report.Report!.Status, report.Report.Reason, ModelArgument = FakeClaude.ReadCapture(CapturePath).Arguments.Contains(Fable) }
            .Should().BeEquivalentTo(new { Status = ReportStatus.Done, Reason = (string?)null, ModelArgument = true }, report.BodyText);
    }

    // AC-4: the job asks Opus, fake-claude's system event names Fable: failed / model_unavailable, the body names both, the report's
    // model is what started.
    [Fact]
    public async Task ModelRefusal_SystemEventNamesAnotherModel_FailedModelUnavailableNamingBoth()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var claude = new ClaudeSettings();

        // Act
        Model report = await RunNodeOnceAsync(Opus, claude, "done", ct: ct);

        // Assert
        new { report.Report!.Status, report.Report.Reason, report.Report.Model, Line = report.BodyText.Split('\n')[0] }.Should().BeEquivalentTo(new
        {
            Status = ReportStatus.Failed,
            Reason = "model_unavailable",
            Model = Fable,
            Line = "model_unavailable: asked " + Opus + ", Claude Code started " + Fable,
        });
    }

    // AC-4: the mismatch stops the real process at once: a run that names another model and then hangs past the job's time limit ends
    // model_unavailable, not timeout, and well before the limit.
    [Fact]
    public async Task ModelRefusal_MismatchThenHang_ProcessStoppedAtOnceNotTimeout()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await MaterialiseScenarioAsync("init-then-hang", lines => [lines[0]], ct);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Act
        Model report = await RunNodeOnceAsync(Opus, new ClaudeSettings(), "init-then-hang", lingerMs: 120_000, ct: ct);

        // Assert
        new { report.Report!.Status, report.Report.Reason, Quick = stopwatch.Elapsed < TimeSpan.FromSeconds(25) }
            .Should().BeEquivalentTo(new { Status = ReportStatus.Failed, Reason = "model_unavailable", Quick = true }, report.BodyText);
    }

    // AC-4: a system event naming the asked model plus a context-size suffix is a match: the run goes on to done.
    [Fact]
    public async Task ModelRefusal_SystemEventNamesAskedModelWithSuffix_RunsDone()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await MaterialiseScenarioAsync(
            "done-suffix",
            lines => [lines[0].Replace("\"model\":\"" + Fable + "\"", "\"model\":\"" + Fable + "[1m]\"", StringComparison.Ordinal), .. lines[1..]],
            ct);

        // Act
        Model report = await RunNodeOnceAsync(Fable, new ClaudeSettings(), "done-suffix", ct: ct);

        // Assert
        new { report.Report!.Status, report.Report.Model }.Should().BeEquivalentTo(new { Status = ReportStatus.Done, Model = Fable + "[1m]" }, report.BodyText);
    }

    // AC-5 (wire): Claude Code's own refusal (the captured stream, exit 1) is failed / model_unavailable with its first error line.
    [Fact]
    public async Task ModelRefusal_ClaudeCodeRefusesTheModel_FailedModelUnavailableWithItsErrorLine()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var claude = new ClaudeSettings();

        // Act
        Model report = await RunNodeOnceAsync(Opus, claude, "model-refused", exitCode: 1, ct: ct);

        // Assert
        new { report.Report!.Status, report.Report.Reason, Line = report.BodyText.Split('\n')[0] }.Should().BeEquivalentTo(new
        {
            Status = ReportStatus.Failed,
            Reason = "model_unavailable",
            Line = "model_unavailable: model " + Opus + " refused by Claude Code: There's an issue with the selected model (claude-opus-0-0). It may not exist or you may not have access to it. Run --model to pick a different model.",
        });
    }

    // The production node composition with the given claude section, a signed job asking the model, one listener pass, the single
    // report on main.
    private async Task<Model> RunNodeOnceAsync(string model, ClaudeSettings claude, string scenario, int lingerMs = 0, int exitCode = 0, CancellationToken ct = default)
    {
        NodeConfiguration configuration = TestHost.NodeConfigurationFor(_harness.Bus, CliHarness.HomeLaptop, claude);
        var environment = new Dictionary<string, string>
        {
            ["ZYGGY_FAKE_CLAUDE_SCENARIO"] = scenario,
            ["ZYGGY_FAKE_CLAUDE_CAPTURE"] = CapturePath,
            ["ZYGGY_FAKE_CLAUDE_LINGER_MS"] = lingerMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["ZYGGY_FAKE_CLAUDE_EXIT"] = exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (Directory.Exists(ScenarioDir))
        {
            environment["ZYGGY_FAKE_CLAUDE_SCENARIO_DIR"] = ScenarioDir;
        }

        await using ServiceProvider services = TestHost.BuildRunnerListener(_harness.Bus, CliHarness.HomeLaptop, _harness.Store, FakeClaude.ExecutablePath, configuration, o =>
        {
            o.NodeDir = _harness.Scratch.File("node-run");
            o.TimeoutOverride = TimeSpan.FromSeconds(30);
            o.ModelEnvironment = environment;
        });
        BusListener listener = services.GetRequiredService<BusListener>();
        (await listener.StartAsync(ct)).Should().Be(BusStartupResult.Clean);
        await _harness.Bus.SeedSignedJobAsync(
            CliHarness.Central, CliHarness.HomeLaptop, TestKeys.Acme1, _harness.Store, JobTask,
            fields: new JobFields(_project.Path, TimeoutMinutes: 2, Model: model), ct: ct);
        (await services.GetRequiredService<IBusProvider>().PullAsync(ct)).Succeeded.Should().BeTrue();
        (await listener.ProcessOnceAsync(ct)).Should().Be(1);

        string folder = BusPaths.Reports(CliHarness.Acme, CliHarness.HomeLaptop);
        string listing = await _harness.Bus.RunGitAsync(_harness.Bus.BareDir, ["ls-tree", "--name-only", "main", folder + "/"], ct);
        string path = listing.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(p => p.EndsWith(".md", StringComparison.Ordinal)).Should().ContainSingle().Subject;
        return await _harness.ParseOnMainAsync(path, ct);
    }

    // A scenario derived from fake-claude's "done" (its lines transformed), in the folder ZYGGY_FAKE_CLAUDE_SCENARIO_DIR points to.
    private async Task MaterialiseScenarioAsync(string name, Func<string[], string[]> transform, CancellationToken ct)
    {
        string[] lines = (await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(FakeClaude.ExecutablePath)!, "scenarios", "done.jsonl"), ct))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Directory.CreateDirectory(ScenarioDir);
        await File.WriteAllTextAsync(Path.Combine(ScenarioDir, name + ".jsonl"), string.Join('\n', transform(lines)) + "\n", new UTF8Encoding(false), ct);
    }
}
