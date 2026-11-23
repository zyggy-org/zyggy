using System.Net;
using System.Net.Sockets;

using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Envelope;
using Zyggy.Core.Registry;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Cli;
using Zyggy.Integration.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Jobs;

/// <summary>
/// Spec 50 slice 2 (Wire): <c>zyggy submit --model</c> through the in-process CLI against the bare repository, the production node
/// composition running <c>tools/fake-claude</c>, and <c>zyggy report</c>. A node of this version publishes the capability <c>model</c> and
/// a job with a model goes only to such a machine (AC-7); the node gives Claude Code <c>--model &lt;id&gt;</c> and never
/// <c>--fallback-model</c> (AC-2); the report carries <c>job_type</c> and <c>zyggy report</c> prints <c>model:</c> and <c>job type:</c> (AC-6).
/// </summary>
public sealed class JobModelWireTests : IAsyncLifetime
{
    // The model fake-claude's "done" scenario names in its system event.
    private const string Fable = "claude-fable-5-1";

    private const string Opus = "claude-opus-5-5";

    private CliHarness _harness = null!;

    private ScratchProject _project = null!;

    private string CapturePath => _harness.Scratch.File("capture.bin");

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

    // AC-7: `zyggy discover` on a node of this version publishes the capability model; Central then submits --model to it, the job on
    // main carries the model, and the confirmation names it.
    [Fact]
    public async Task SubmitModel_TargetPublishedByThisVersion_JobOnMainCarriesModelConfirmationNamesIt()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (await _harness.RunAsync(["discover"], _harness.HomeLaptopNodeJson, extraEnvironment: NoNode(), ct: ct)).Exit.Should().Be(0);
        await PullSenderAsync(ct);

        // Act
        var (exit, console) = await _harness.RunAsync(["submit", "--to", "home-laptop", "--project", "shop", "--model", Opus, "Write the spec.\n"], _harness.CentralNodeJson, ct: ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        string[] lines = console.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Model job = await _harness.ParseOnMainAsync(lines[0].Split('(', ')')[1], ct);
        RegistryDocument published = RegistryDocument.Parse(await _harness.Bus.ShowBytesAsync(BusPaths.Registry(CliHarness.Acme, CliHarness.HomeLaptop), ct)).Document!;
        new { Capabilities = published.Capabilities.Contains(RegistryDocument.ModelCapability), job.Job!.Model, job.Job.JobType, Confirmation = lines[1] }
            .Should().BeEquivalentTo(new { Capabilities = true, Model = Opus, JobType = (string?)null, Confirmation = "model: " + Opus + " (override)" });
    }

    // AC-7: a target whose registry file lacks the capability (an older node) is refused with exit 3 and the upgrade line; nothing written.
    [Fact]
    public async Task SubmitModel_TargetRegistryWithoutModelCapability_ExitThreeNothingWritten()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await SeedRegistryAsync(CliHarness.WorkLaptop, [RegistryDocument.CommandCapability, RegistryDocument.FetchCapability], ct);
        int before = await _harness.CommitCountOnMainAsync(ct);

        // Act
        var (exit, console) = await _harness.RunAsync(["submit", "--to", "work-laptop", "--project", "shop", "--model", Opus, "Write the spec.\n"], _harness.CentralNodeJson, ct: ct);

        // Assert
        new { Exit = exit, Commits = await _harness.CommitCountOnMainAsync(ct), console.Stderr, console.Stdout }.Should().BeEquivalentTo(new
        {
            Exit = 3,
            Commits = before,
            Stderr = "zyggy: refused: work-laptop does not run jobs with a model yet (no capability model); upgrade it or submit without --model\n",
            Stdout = string.Empty,
        });
    }

    // AC-7: without --model the same older target gets the job as today: one confirmation line, no model field.
    [Fact]
    public async Task SubmitWithoutModel_TargetRegistryWithoutModelCapability_SubmittedAsToday()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await SeedRegistryAsync(CliHarness.HomeLaptop, [RegistryDocument.CommandCapability, RegistryDocument.FetchCapability], ct);

        // Act
        var (exit, console) = await _harness.RunAsync(["submit", "--to", "home-laptop", "--project", "shop", "Write the spec.\n"], _harness.CentralNodeJson, ct: ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        string[] lines = console.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Model job = await _harness.ParseOnMainAsync(lines[0].Split('(', ')')[1], ct);
        new { Lines = lines.Length, job.Job!.Model, job.Job.JobType, ModelKey = job.FrontMatter.Entries.ContainsKey("model") }
            .Should().BeEquivalentTo(new { Lines = 1, Model = (string?)null, JobType = (string?)null, ModelKey = false });
    }

    // AC-1 / AC-9 edge cases on the verb: an alias, a fetch or command job, or a mandate with --model is a usage error; nothing written.
    [Theory]
    [InlineData("--model", "opus")]
    [InlineData("--model", "sonnet[1m]")]
    [InlineData("--fetch", "docs/plan.md")]
    [InlineData("--command", "run-tests")]
    [InlineData("--mandate", "01M84R1Z6HHGTZBKNF0RR8ZXWY")]
    public async Task SubmitModel_AliasOrModelFreeKindOrMandate_ExitTwoNothingWritten(string option, string value)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await SeedRegistryAsync(CliHarness.HomeLaptop, [RegistryDocument.CommandCapability, RegistryDocument.FetchCapability, RegistryDocument.ModelCapability], ct);
        int before = await _harness.CommitCountOnMainAsync(ct);
        string[] args = option == "--model"
            ? ["submit", "--to", "home-laptop", "--project", "shop", option, value, "Write the spec.\n"]
            : option == "--mandate"
                ? ["submit", "--to", "home-laptop", option, value, "--kind", "build", "--step", "one", "--model", Opus]
                : ["submit", "--to", "home-laptop", "--project", "shop", option, value, "--model", Opus];

        // Act
        var (exit, console) = await _harness.RunAsync(args, _harness.CentralNodeJson, ct: ct);

        // Assert
        new { Exit = exit, Commits = await _harness.CommitCountOnMainAsync(ct), NamesModel = console.Stderr.Contains("model", StringComparison.Ordinal) }
            .Should().BeEquivalentTo(new { Exit = 2, Commits = before, NamesModel = true }, console.Stderr);
    }

    // AC-2 (Wire) and AC-6: submitted with --model, the production listener gives fake-claude `--model <id>` as two arguments and never
    // --fallback-model; `zyggy report` prints the model that ran and no job type.
    [Fact]
    public async Task SubmitModel_NodeRunsJob_ClaudeGetsModelArgumentReportShowsIt()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await SeedRegistryAsync(CliHarness.HomeLaptop, [RegistryDocument.ModelCapability], ct);
        await using ServiceProvider services = Node();
        BusListener listener = services.GetRequiredService<BusListener>();
        (await listener.StartAsync(ct)).Should().Be(BusStartupResult.Clean);
        var (submitExit, submit) = await _harness.RunAsync(["submit", "--to", "home-laptop", "--project", _project.Path, "--model", Fable, "Read README.md and finish with a REPORT section.\n"], _harness.CentralNodeJson, ct: ct);
        submitExit.Should().Be(0, submit.Stderr);
        string id = submit.Stdout.Split(' ')[1];

        // Act
        (await services.GetRequiredService<IBusProvider>().PullAsync(ct)).Succeeded.Should().BeTrue();
        (await listener.ProcessOnceAsync(ct)).Should().Be(1);

        // Assert
        IReadOnlyList<string> arguments = FakeClaude.ReadCapture(CapturePath).Arguments;
        int at = arguments.ToList().IndexOf("--model");
        await _harness.Bus.PullCloneAsync(ct);
        var (_, report) = await _harness.RunAsync(["report", id], _harness.HomeLaptopNodeJson, ct: ct);
        new
        {
            Model = at >= 0 && at + 1 < arguments.Count ? arguments[at + 1] : null,
            ModelFlags = arguments.Count(a => a == "--model"),
            Fallback = arguments.Any(a => a.Contains("fallback", StringComparison.Ordinal)),
            Lines = report.Stdout.Contains("\ntokens: ", StringComparison.Ordinal) && report.Stdout.Contains("\nmodel: " + Fable + "\njob type: -\nfrom: ", StringComparison.Ordinal),
        }.Should().BeEquivalentTo(new { Model = Fable, ModelFlags = 1, Fallback = false, Lines = true }, report.Stdout + report.Stderr);
    }

    // AC-2: a job without a model gives exactly today's arguments: no --model at all.
    [Fact]
    public async Task SubmitWithoutModel_NodeRunsJob_NoModelArgument()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using ServiceProvider services = Node();
        BusListener listener = services.GetRequiredService<BusListener>();
        (await listener.StartAsync(ct)).Should().Be(BusStartupResult.Clean);
        (await _harness.RunAsync(["submit", "--to", "home-laptop", "--project", _project.Path, "Read README.md and finish with a REPORT section.\n"], _harness.CentralNodeJson, ct: ct)).Exit.Should().Be(0);

        // Act
        (await services.GetRequiredService<IBusProvider>().PullAsync(ct)).Succeeded.Should().BeTrue();
        (await listener.ProcessOnceAsync(ct)).Should().Be(1);

        // Assert
        FakeClaude.ReadCapture(CapturePath).Arguments.Should().NotContain(a => a == "--model" || a.Contains("fallback", StringComparison.Ordinal));
    }

    // AC-6 (Wire): a job with job_type run by the production node: its report carries job_type and `zyggy report` prints it.
    [Fact]
    public async Task JobWithJobType_NodeRunsJob_ReportCarriesJobTypeAndVerbPrintsIt()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using ServiceProvider services = Node();
        BusListener listener = services.GetRequiredService<BusListener>();
        (await listener.StartAsync(ct)).Should().Be(BusStartupResult.Clean);
        SeededJob job = await _harness.Bus.SeedSignedJobAsync(
            CliHarness.Central, CliHarness.HomeLaptop, TestKeys.Acme1, _harness.Store, "Read README.md and finish with a REPORT section.\n",
            fields: new JobFields(_project.Path, TimeoutMinutes: 2, Model: Fable, JobType: "spec"), ct: ct);

        // Act
        (await services.GetRequiredService<IBusProvider>().PullAsync(ct)).Succeeded.Should().BeTrue();
        (await listener.ProcessOnceAsync(ct)).Should().Be(1);

        // Assert
        await _harness.Bus.PullCloneAsync(ct);
        var (exit, report) = await _harness.RunAsync(["report", job.Id.Value], _harness.HomeLaptopNodeJson, ct: ct);
        new { Exit = exit, Lines = report.Stdout.Contains("\nmodel: " + Fable + "\njob type: spec\nfrom: home-laptop\n", StringComparison.Ordinal) }
            .Should().BeEquivalentTo(new { Exit = 0, Lines = true }, report.Stdout + report.Stderr);
    }

    // AC-6: while the job is on the bus and the report names another model than it asked for, the line reads `model: <ran> (asked <id>)`.
    [Fact]
    public async Task Report_JobOnBusAskedAnotherModel_ModelLineMarksTheMismatch()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        SeededJob job = await _harness.Bus.SeedSignedJobAsync(
            CliHarness.Central, CliHarness.HomeLaptop, TestKeys.Acme1, _harness.Store, "Write the spec.\n",
            fields: new JobFields("shop", TimeoutMinutes: 2, Model: Opus, JobType: "spec"), ct: ct);
        await _harness.Bus.SeedSignedReportAsync(
            CliHarness.HomeLaptop, CliHarness.Central, job.Id, TestKeys.Acme1, _harness.Store, "REPORT\nsummary: done\n",
            fields: new ReportFields(ReportStatus.Done, Model: Fable, JobType: "spec"), created: DateTimeOffset.UtcNow, ct: ct);
        await _harness.Bus.PullCloneAsync(ct);

        // Act
        var (exit, console) = await _harness.RunAsync(["report", job.Id.Value], _harness.HomeLaptopNodeJson, ct: ct);

        // Assert
        new { Exit = exit, Lines = console.Stdout.Contains("\ntokens: -\nmodel: " + Fable + " (asked " + Opus + ")\njob type: spec\nfrom: ", StringComparison.Ordinal) }
            .Should().BeEquivalentTo(new { Exit = 0, Lines = true }, console.Stdout + console.Stderr);
    }

    // AC-6: a report with the asked model plus a context-size suffix is a match: no mark.
    [Fact]
    public async Task Report_JobOnBusRanWithSuffix_NoMismatchMark()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        SeededJob job = await _harness.Bus.SeedSignedJobAsync(
            CliHarness.Central, CliHarness.HomeLaptop, TestKeys.Acme1, _harness.Store, "Write the spec.\n",
            fields: new JobFields("shop", TimeoutMinutes: 2, Model: Opus), ct: ct);
        await _harness.Bus.SeedSignedReportAsync(
            CliHarness.HomeLaptop, CliHarness.Central, job.Id, TestKeys.Acme1, _harness.Store, "REPORT\nsummary: done\n",
            fields: new ReportFields(ReportStatus.Done, Model: Opus + "[1m]"), created: DateTimeOffset.UtcNow, ct: ct);
        await _harness.Bus.PullCloneAsync(ct);

        // Act
        var (exit, console) = await _harness.RunAsync(["report", job.Id.Value], _harness.HomeLaptopNodeJson, ct: ct);

        // Assert
        new { Exit = exit, Lines = console.Stdout.Contains("\nmodel: " + Opus + "[1m]\njob type: -\nfrom: ", StringComparison.Ordinal) }
            .Should().BeEquivalentTo(new { Exit = 0, Lines = true }, console.Stdout + console.Stderr);
    }

    private ServiceProvider Node()
    {
        NodeConfiguration configuration = TestHost.NodeConfigurationFor(_harness.Bus, CliHarness.HomeLaptop);
        return TestHost.BuildRunnerListener(_harness.Bus, CliHarness.HomeLaptop, _harness.Store, FakeClaude.ExecutablePath, configuration, o =>
        {
            o.NodeDir = _harness.Scratch.File("node-run");
            o.TimeoutOverride = TimeSpan.FromSeconds(30);
            o.ModelEnvironment = new Dictionary<string, string> { ["ZYGGY_FAKE_CLAUDE_SCENARIO"] = "done", ["ZYGGY_FAKE_CLAUDE_CAPTURE"] = CapturePath };
        });
    }

    // A registry file as a node publishes it, with the capabilities given (an older node's file lacks model).
    private Task SeedRegistryAsync(MachineName machine, IReadOnlyList<string> capabilities, CancellationToken ct)
    {
        var document = new RegistryDocument(CliHarness.Acme, machine, RegistryOsExtensions.Current(), null, null, null, []) { Capabilities = capabilities };
        return _harness.Bus.SeedRawFileAsync(BusPaths.Registry(CliHarness.Acme, machine), document.Serialize(), "acme registry " + machine.Value, ct);
    }

    private Task<string> PullSenderAsync(CancellationToken ct) => _harness.Bus.RunGitAsync(_harness.Bus.SenderDir, ["pull", "--rebase", "origin", "main"], ct);

    // No node answers /health: discover publishes without a health verdict (as RegistryPublishRoundTripTests).
    private static Dictionary<string, string?> NoNode()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return new() { ["ZYGGY_HEALTH_URL"] = $"http://127.0.0.1:{port}/health" };
    }
}
