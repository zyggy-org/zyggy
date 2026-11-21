using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Envelope;
using Zyggy.Core.Policy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Cli;
using Zyggy.Integration.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Jobs;

/// <summary>
/// Spec 59 slice 2 (Wire): a signed <c>command</c> job through the production node composition. The allow-list is the node's own
/// <c>commands</c> (real <c>git</c> as the program, run in the project folder without a shell); <c>tools/fake-claude</c> is configured but
/// never started. Exit 0 is <c>done</c>, a non-zero exit <c>failed</c>/<c>command_failed</c> (AC-4); a name the node does not list is
/// <c>rejected</c>/<c>schema_unsupported</c> (AC-4, AC-15); <c>zyggy submit --command</c> refuses a machine that does not advertise the kind (AC-5).
/// </summary>
public sealed class CommandJobTests : IAsyncLifetime
{
    private static readonly Dictionary<string, NodeCommand> Allowed = new(StringComparer.Ordinal)
    {
        ["inside"] = new(["git", "rev-parse", "--is-inside-work-tree"], 1),
        ["broken"] = new(["git", "no-such-verb"], 1),
    };

    private CliHarness _harness = null!;

    private WorktreeProject _project = null!;

    private string CapturePath => _harness.Scratch.File("capture.bin");

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        _harness = await CliHarness.CreateAsync(ct);
        _project = await WorktreeProject.CreateAsync(_harness.Bus, ct);
    }

    public async ValueTask DisposeAsync()
    {
        _project?.Dispose();
        await _harness.DisposeAsync();
    }

    // AC-4: exit 0 is done, the output is the allow-listed argv's own, and the model never started.
    [Fact]
    public async Task CommandJob_AllowListedExitZero_ReportDoneWithOutputModelNeverStarted()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;

        // Act
        SeededJob job = await ProcessAsync("inside", ct);

        // Assert
        Model report = await ReportForAsync(job, ct);
        new { report.Report!.Status, Output = report.BodyText.Contains("true", StringComparison.Ordinal), ModelStarted = File.Exists(CapturePath) }
            .Should().BeEquivalentTo(new { Status = ReportStatus.Done, Output = true, ModelStarted = false }, report.BodyText);
    }

    // AC-4: a non-zero exit is failed with reason command_failed.
    [Fact]
    public async Task CommandJob_AllowListedNonZeroExit_FailedCommandFailed()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;

        // Act
        SeededJob job = await ProcessAsync("broken", ct);

        // Assert
        Model report = await ReportForAsync(job, ct);
        new { report.Report!.Status, report.Report.Reason, ModelStarted = File.Exists(CapturePath) }
            .Should().BeEquivalentTo(new { Status = ReportStatus.Failed, Reason = "command_failed", ModelStarted = false }, report.BodyText);
    }

    // AC-4 / AC-15: a name not in this node's commands is refused before any process starts.
    [Fact]
    public async Task CommandJob_NameNotInNodeJson_RejectedSchemaUnsupported()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;

        // Act
        SeededJob job = await ProcessAsync("not-listed", ct);

        // Assert
        Model report = await ReportForAsync(job, ct);
        new { report.Report!.Status, report.Report.Reason }.Should().BeEquivalentTo(new { Status = ReportStatus.Rejected, Reason = "schema_unsupported" }, report.BodyText);
    }

    // AC-5: the CLI refuses --command to a machine whose registry does not advertise command; nothing is written to the bus.
    [Fact]
    public async Task CommandJob_SubmitToMachineWithoutCommandCapability_RefusedNothingOnTheBus()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        int before = await _harness.CommitCountOnMainAsync(ct);

        // Act
        var (exit, console) = await _harness.RunAsync(
            ["submit", "--to", "home-laptop", "--project", "shop", "--command", "inside"], _harness.CentralNodeJson, ct: ct);

        // Assert
        new { Exit = exit, Commits = await _harness.CommitCountOnMainAsync(ct), Refused = console.Stderr.Contains("kind", StringComparison.Ordinal) }
            .Should().BeEquivalentTo(new { Exit = 2, Commits = before, Refused = true }, console.Stderr);
    }

    private async Task<SeededJob> ProcessAsync(string name, CancellationToken ct)
    {
        NodeConfiguration configuration = TestHost.NodeConfigurationFor(_harness.Bus, CliHarness.HomeLaptop) with { Commands = Allowed };
        await using ServiceProvider services = TestHost.BuildRunnerListener(_harness.Bus, CliHarness.HomeLaptop, _harness.Store, FakeClaude.ExecutablePath, configuration, o =>
        {
            o.NodeDir = _project.NodeDir;
            o.TimeoutOverride = TimeSpan.FromSeconds(30);
            o.ModelEnvironment = new Dictionary<string, string> { ["ZYGGY_FAKE_CLAUDE_SCENARIO"] = "done", ["ZYGGY_FAKE_CLAUDE_CAPTURE"] = CapturePath };
        });
        BusListener listener = services.GetRequiredService<BusListener>();
        (await listener.StartAsync(ct)).Should().Be(BusStartupResult.Clean);
        var fields = new JobFields(_project.Path, TimeoutMinutes: 2, Kind: JobKind.Command, Command: name);
        SeededJob job = await _harness.Bus.SeedSignedJobAsync(CliHarness.Central, CliHarness.HomeLaptop, TestKeys.Acme1, _harness.Store, "command " + name + "\n", fields: fields, ct: ct);
        (await services.GetRequiredService<IBusProvider>().PullAsync(ct)).Succeeded.Should().BeTrue();
        (await listener.ProcessOnceAsync(ct)).Should().Be(1);
        return job;
    }

    private async Task<Model> ReportForAsync(SeededJob job, CancellationToken ct)
    {
        string output = await _harness.Bus.RunGitAsync(_harness.Bus.BareDir, ["ls-tree", "--name-only", "main", BusPaths.Reports(CliHarness.Acme, CliHarness.HomeLaptop) + "/"], ct);
        var reports = new List<Model>();
        foreach (var path in output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(p => p.EndsWith(".md", StringComparison.Ordinal)))
        {
            Model report = await _harness.ParseOnMainAsync(path, ct);
            if (report.Header.InReplyTo == job.Id)
            {
                reports.Add(report);
            }
        }

        return reports.Should().ContainSingle().Subject;
    }
}
