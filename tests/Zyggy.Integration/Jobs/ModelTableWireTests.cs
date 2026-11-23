using Zyggy.Core.Bus;
using Zyggy.Core.Registry;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Cli;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Jobs;

/// <summary>
/// Spec 50 slice 6 (Wire): Central's table <c>instance/models.json</c> read by the real verbs through the in-process CLI against the
/// bare repository. <c>zyggy models</c> prints it (AC-8); <c>zyggy submit --type</c> takes a row, <c>--model</c> overrides it and must be on
/// the allow-list, neither prints <c>model: node default</c> (AC-9). The instance folder is given as <c>ZYGGY_INSTANCE_DIR</c>.
/// </summary>
public sealed class ModelTableWireTests : IAsyncLifetime
{
    private const string Opus = "claude-opus-5-5";

    private const string Fable = "claude-fable-5-2";

    private static readonly string[] NodeDefaultLine = ["model: node default"];

    private CliHarness _harness = null!;

    private string InstanceDir => _harness.Scratch.File("instance");

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        _harness = await CliHarness.CreateAsync(ct);
        var document = new RegistryDocument(CliHarness.Acme, CliHarness.HomeLaptop, RegistryOsExtensions.Current(), null, null, null, [])
        {
            Capabilities = [RegistryDocument.ModelCapability, RegistryDocument.FetchCapability, RegistryDocument.CommandCapability],
        };
        await _harness.Bus.SeedRawFileAsync(BusPaths.Registry(CliHarness.Acme, CliHarness.HomeLaptop), document.Serialize(), "acme registry home-laptop", ct);
    }

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    // AC-8: `zyggy models` on the owner's starting file: the allow-list, one line per type in file order, the supervisor, the source.
    [Fact]
    public async Task Models_OwnersStartingTable_PrintsAllowListTypesSupervisorAndSource()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        WriteTable(File.ReadAllText(Path.Combine(Golden.Directory, "models", "models.json")));

        // Act
        var (exit, console) = await _harness.RunAsync(["models"], _harness.CentralNodeJson, extraEnvironment: Instance(), ct: ct);

        // Assert
        new { Exit = exit, console.Stdout }.Should().BeEquivalentTo(new
        {
            Exit = 0,
            Stdout = $"models: {Opus}, {Fable}\nspec  {Fable}\nimplementation  {Opus}\nsupervisor  default\nfrom: instance/models.json\n",
        }, console.Stderr);
    }

    // AC-8: an invalid table makes `zyggy models` exit 2 with one line naming the key.
    [Theory]
    [InlineData("{ \"models\": [\"claude-opus-5-5\"", "instance/models.json: models.json is not valid JSON")]
    [InlineData("{ \"models\": [\"opus\"] }", "instance/models.json: models[0] must be a full Claude Code model id")]
    [InlineData("{ \"models\": [\"claude-opus-5-5\"], \"jobTypes\": { \"spec\": \"claude-fable-5-2\" } }", "instance/models.json: jobTypes.spec model claude-fable-5-2 is not on models")]
    [InlineData("{ \"models\": [\"claude-opus-5-5\"], \"models\": [\"claude-fable-5-2\"] }", "instance/models.json: models appears twice")]
    public async Task Models_InvalidTable_ExitTwoNamingTheKey(string json, string line)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        WriteTable(json);

        // Act
        var (exit, console) = await _harness.RunAsync(["models"], _harness.CentralNodeJson, extraEnvironment: Instance(), ct: ct);

        // Assert
        new { Exit = exit, Starts = console.Stderr.StartsWith("zyggy: " + line, StringComparison.Ordinal), console.Stdout }
            .Should().BeEquivalentTo(new { Exit = 2, Starts = true, Stdout = string.Empty }, console.Stderr);
    }

    // AC-8: on a machine without the instance file, node.json claude.models is the allow-list and the source line says so.
    [Fact]
    public async Task Models_NoTableNodeJsonModels_PrintsTheNodeList()
    {
        // Act
        var ct = TestContext.Current.CancellationToken;
        var (exit, console) = await _harness.RunAsync(["models"], NodeJsonWithModels(Opus), ct: ct);

        // Assert
        new { Exit = exit, console.Stdout }.Should().BeEquivalentTo(new
        {
            Exit = 0,
            Stdout = $"models: {Opus}\nno job types\nsupervisor  default\nfrom: node.json claude.models\n",
        }, console.Stderr);
    }

    // AC-9: --type spec puts the row's model and job_type spec on the job and prints `model: <id> (job type spec)`.
    [Fact]
    public async Task SubmitType_RowInTheTable_JobCarriesItsModelAndJobType()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        WriteStartingTable();

        // Act
        var (exit, console) = await SubmitAsync(["--type", "spec"], ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        string[] lines = console.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Model job = await _harness.ParseOnMainAsync(lines[0].Split('(', ')')[1], ct);
        new { job.Job!.Model, job.Job.JobType, Confirmation = lines[1] }
            .Should().BeEquivalentTo(new { Model = Fable, JobType = "spec", Confirmation = $"model: {Fable} (job type spec)" });
    }

    // AC-9: --model overrides the row for one job (job_type kept) and prints `(override)`.
    [Fact]
    public async Task SubmitTypeAndModel_ModelOnTheList_OverridesTheRow()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        WriteStartingTable();

        // Act
        var (exit, console) = await SubmitAsync(["--type", "spec", "--model", Opus], ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        string[] lines = console.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Model job = await _harness.ParseOnMainAsync(lines[0].Split('(', ')')[1], ct);
        new { job.Job!.Model, job.Job.JobType, Confirmation = lines[1] }
            .Should().BeEquivalentTo(new { Model = Opus, JobType = "spec", Confirmation = $"model: {Opus} (override)" });
    }

    // AC-9: neither option: no model, no job_type, and the confirmation says the node's default.
    [Fact]
    public async Task SubmitNeither_TableExists_NoFieldsNodeDefaultLine()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        WriteStartingTable();

        // Act
        var (exit, console) = await SubmitAsync([], ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        string[] lines = console.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Model job = await _harness.ParseOnMainAsync(lines[0].Split('(', ')')[1], ct);
        new { ModelKey = job.FrontMatter.Entries.ContainsKey("model"), TypeKey = job.FrontMatter.Entries.ContainsKey("job_type"), Lines = lines.Skip(1).ToArray() }
            .Should().BeEquivalentTo(new { ModelKey = false, TypeKey = false, Lines = NodeDefaultLine });
    }

    // AC-9: on a laptop without the instance file, --model is checked against node.json claude.models.
    [Fact]
    public async Task SubmitModel_NoTableNodeJsonModelsListsIt_Submitted()
    {
        // Act
        var ct = TestContext.Current.CancellationToken;
        var (exit, console) = await _harness.RunAsync(["submit", "--to", "home-laptop", "--project", "shop", "--model", Opus, "Write the spec.\n"], NodeJsonWithModels(Opus), ct: ct);

        // Assert
        new { Exit = exit, Confirmation = console.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Last() }
            .Should().BeEquivalentTo(new { Exit = 0, Confirmation = $"model: {Opus} (override)" }, console.Stderr);
    }

    // AC-9: refusals, all exit 2 with one line naming the option and what is allowed, nothing written: an unknown type lists the
    // types, an id off the list lists the list, --model without any allow-list, --type without a table, an invalid table names its key,
    // a fetch or command job with --type runs no model.
    [Theory]
    [InlineData("table", new[] { "--type", "testing" }, "zyggy: type: type testing is not in the model table; types: spec, implementation\n")]
    [InlineData("table", new[] { "--model", "claude-sonnet-5-5" }, "zyggy: model: model claude-sonnet-5-5 is not on the allow-list (instance/models.json models): claude-opus-5-5, claude-fable-5-2\n")]
    [InlineData("none", new[] { "--model", "claude-opus-5-5" }, "zyggy: model: --model needs an allow-list: instance/models.json models or node.json claude.models\n")]
    [InlineData("none", new[] { "--type", "spec" }, "zyggy: type: --type needs a model table: no instance/models.json on this machine\n")]
    [InlineData("invalid", new[] { "--type", "spec" }, "zyggy: instance/models.json: supervisor model claude-sonnet-5-5 not allowed: not on models\n")]
    [InlineData("table", new[] { "--type", "spec", "--fetch", "docs/plan.md" }, "zyggy: model: a fetch or command job runs no model: no --model\n")]
    [InlineData("table", new[] { "--type", "spec", "--command", "run-tests" }, "zyggy: model: a fetch or command job runs no model: no --model\n")]
    public async Task Submit_TypeOrModelRefused_ExitTwoNothingWritten(string table, string[] options, string stderr)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        if (table == "table")
        {
            WriteStartingTable();
        }
        else if (table == "invalid")
        {
            WriteTable("{ \"models\": [\"claude-opus-5-5\"], \"supervisor\": \"claude-sonnet-5-5\" }");
        }

        int before = await _harness.CommitCountOnMainAsync(ct);

        // Act
        var (exit, console) = await SubmitAsync(options, ct);

        // Assert
        new { Exit = exit, Commits = await _harness.CommitCountOnMainAsync(ct), console.Stderr, console.Stdout }
            .Should().BeEquivalentTo(new { Exit = 2, Commits = before, Stderr = stderr, Stdout = string.Empty });
    }

    private Task<(int Exit, VerbConsole Console)> SubmitAsync(string[] options, CancellationToken ct) =>
        _harness.RunAsync(["submit", "--to", "home-laptop", "--project", "shop", .. options, "Write the spec.\n"], _harness.CentralNodeJson, extraEnvironment: Instance(), ct: ct);

    private Dictionary<string, string?> Instance() => new() { ["ZYGGY_INSTANCE_DIR"] = InstanceDir };

    private void WriteStartingTable() => WriteTable(File.ReadAllText(Path.Combine(Golden.Directory, "models", "models.json")));

    private void WriteTable(string json)
    {
        Directory.CreateDirectory(InstanceDir);
        File.WriteAllText(Path.Combine(InstanceDir, "models.json"), json);
    }

    // Central's node.json with `claude.models` added (the laptop's allow-list when there is no instance file).
    private string NodeJsonWithModels(string model)
    {
        string path = _harness.Scratch.File("node-with-models.json");
        string text = File.ReadAllText(_harness.CentralNodeJson).Replace("\"claude\": { ", $"\"claude\": {{ \"models\": [\"{model}\"], ", StringComparison.Ordinal);
        File.WriteAllText(path, text);
        return path;
    }
}
