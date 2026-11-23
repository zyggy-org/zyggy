using Zyggy.Core.Envelope;
using Zyggy.Core.Models;

namespace Zyggy.Core.Tests.Models;

// Spec 50 slice 5, AC-8 (unit): Central's table `instance/models.json` is read with the rules of "Central's table" — the allow-list,
// the job-type rows, the supervisor — and every invalid case names its key; nothing is guessed, nothing is ignored.
public sealed class ModelTableTests
{
    private const string Opus = "claude-opus-5-5";

    private const string Fable = "claude-fable-5-2";

    private static string Example => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "golden", "models", "models.json"));

    // AC-8: the checked-in example is the owner's starting file.
    [Fact]
    public void Parse_CheckedInExample_OwnersStartingValues()
    {
        // Act
        ModelTableResult result = ModelTable.Parse(Example);

        // Assert
        result.Table.Should().BeEquivalentTo(
            new ModelTable([Opus, Fable], [new ModelTableRow("spec", Fable), new ModelTableRow("implementation", Opus)], null),
            o => o.WithStrictOrdering());
    }

    // AC-8
    [Fact]
    public void Parse_SupervisorSet_KeepsIt()
    {
        // Act
        ModelTableResult result = ModelTable.Parse($$"""{ "models": ["{{Opus}}"], "supervisor": "{{Opus}}" }""");

        // Assert
        result.Table.Should().BeEquivalentTo(new ModelTable([Opus], [], Opus));
    }

    // AC-8: each invalid case refuses the whole file with the key named.
    [Theory]
    [InlineData("not json", "models.json", "is not valid JSON")]
    [InlineData("[]", "models.json", "is not a JSON object")]
    [InlineData("""{ "jobTypes": {} }""", "models", "is missing")]
    [InlineData("""{ "models": [] }""", "models", "must be an array of 1–20 model ids")]
    [InlineData("""{ "models": "claude-opus-5-5" }""", "models", "must be an array of 1–20 model ids")]
    [InlineData("""{ "models": ["claude-opus-5-5", "opus"] }""", "models[1]", JobModelRules.ModelRule)]
    [InlineData("""{ "models": ["claude-opus-5-5", "Claude Opus"] }""", "models[1]", JobModelRules.ModelRule)]
    [InlineData("""{ "models": ["claude-opus-5-5", 5] }""", "models[1]", JobModelRules.ModelRule)]
    [InlineData("""{ "models": ["claude-opus-5-5", "claude-opus-5-5"] }""", "models[1]", "appears twice")]
    [InlineData("""{ "models": ["claude-opus-5-5"], "jobTypes": [] }""", "jobTypes", "must be an object")]
    [InlineData("""{ "models": ["claude-opus-5-5"], "jobTypes": { "spec": "claude-fable-5-2" } }""", "jobTypes.spec", "model claude-fable-5-2 is not on models")]
    [InlineData("""{ "models": ["claude-opus-5-5"], "jobTypes": { "spec": "sonnet" } }""", "jobTypes.spec", JobModelRules.ModelRule)]
    [InlineData("""{ "models": ["claude-opus-5-5"], "jobTypes": { "spec": 1 } }""", "jobTypes.spec", JobModelRules.ModelRule)]
    [InlineData("""{ "models": ["claude-opus-5-5"], "jobTypes": { "Spec Writing": "claude-opus-5-5" } }""", "jobTypes.Spec Writing", "is not a job type label (a–z 0–9 -, 1–63)")]
    [InlineData("""{ "models": ["claude-opus-5-5"], "jobTypes": { "spec": "claude-opus-5-5", "spec": "claude-opus-5-5" } }""", "jobTypes.spec", "appears twice")]
    [InlineData("""{ "models": ["claude-opus-5-5"], "supervisor": "claude-fable-5-2" }""", "supervisor", "model claude-fable-5-2 not allowed: not on models")]
    [InlineData("""{ "models": ["claude-opus-5-5"], "supervisor": "default" }""", "supervisor", JobModelRules.ModelRule)]
    [InlineData("""{ "models": ["claude-opus-5-5"], "supervisor": 3 }""", "supervisor", JobModelRules.ModelRule)]
    [InlineData("""{ "models": ["claude-opus-5-5"], "fallback": "claude-opus-5-5" }""", "fallback", "is not a models.json key")]
    [InlineData("""{ "models": ["claude-opus-5-5"], "models": ["claude-fable-5-2"] }""", "models", "appears twice")]
    [InlineData("""{ "models": ["claude-opus-5-5"], "jobTypes": {}, "jobTypes": { "spec": "claude-opus-5-5" } }""", "jobTypes", "appears twice")]
    [InlineData("""{ "models": ["claude-opus-5-5"], "supervisor": null, "supervisor": "claude-opus-5-5" }""", "supervisor", "appears twice")]
    public void Parse_InvalidFile_RefusedNamingTheKey(string json, string key, string message)
    {
        // Act
        ModelTableResult result = ModelTable.Parse(json);

        // Assert
        result.Should().BeEquivalentTo(new ModelTableResult(null, key, $"instance/models.json: {key} {message}"));
    }

    // AC-8: the allow-list holds at most 20 ids.
    [Fact]
    public void Parse_TwentyOneModels_RefusedModels()
    {
        // Arrange
        string ids = string.Join(", ", Enumerable.Range(1, 21).Select(n => $"\"claude-test-{n}\""));

        // Act
        ModelTableResult result = ModelTable.Parse($$"""{ "models": [{{ids}}] }""");

        // Assert
        result.ErrorKey.Should().Be("models");
    }

    // AC-8: a missing file is "no table", not an error.
    [Fact]
    public void Load_NoFile_NoTableNoError()
    {
        // Act
        ModelTableResult result = ModelTable.Load("/instance/models.json", _ => null);

        // Assert
        result.Should().BeEquivalentTo(new ModelTableResult(null, null, null));
    }

    // AC-8: a present file goes through the same rules.
    [Fact]
    public void Load_InvalidFile_RefusedNamingTheKey()
    {
        // Act
        ModelTableResult result = ModelTable.Load("/instance/models.json", _ => """{ "models": ["opus"] }""");

        // Assert
        result.ErrorKey.Should().Be("models[0]");
    }

    // AC-8: Central's verbs read the file from the instance directory, as dream.json and archive.json.
    [Theory]
    [InlineData("/srv/instance", null, "/srv/instance", "")]
    [InlineData(null, "/srv/core", "/srv/core", "instance")]
    [InlineData("/srv/instance", "/srv/core", "/srv/instance", "")]
    public void PathIn_InstanceOrProjectDirectory_ModelsJsonInIt(string? instance, string? project, string directory, string folder)
    {
        // Arrange
        var environment = new Dictionary<string, string?> { ["ZYGGY_INSTANCE_DIR"] = instance, ["CLAUDE_PROJECT_DIR"] = project };

        // Act
        string? path = ModelTable.PathIn(environment);

        // Assert
        path.Should().Be(Path.Join(directory, folder, "models.json"));
    }

    // AC-8: neither directory: no file to read.
    [Fact]
    public void PathIn_NoDirectory_Null()
    {
        // Act
        string? path = ModelTable.PathIn(new Dictionary<string, string?>());

        // Assert
        path.Should().BeNull();
    }

    // AC-8: `zyggy models` prints the allow-list, one line per job type in file order, the supervisor and where it read them.
    [Fact]
    public void Describe_Table_AllowListTypesSupervisorAndSource()
    {
        // Arrange
        ModelTable table = ModelTable.Parse(Example).Table!;

        // Act
        string text = ModelChoice.Describe(table, nodeModels: null);

        // Assert
        text.Should().Be(
            "models: claude-opus-5-5, claude-fable-5-2\n"
            + "spec  claude-fable-5-2\n"
            + "implementation  claude-opus-5-5\n"
            + "supervisor  default\n"
            + "from: instance/models.json\n");
    }

    // AC-8: without the instance file the machine's node.json list is the allow-list, and there are no job types.
    [Fact]
    public void Describe_NodeListOnly_AllowListAndSource()
    {
        // Act
        string text = ModelChoice.Describe(null, [Opus]);

        // Assert
        text.Should().Be("models: claude-opus-5-5\nno job types\nsupervisor  default\nfrom: node.json claude.models\n");
    }

    // AC-8: neither list: jobs run on the node's default model.
    [Fact]
    public void Describe_NoList_SaysJobsRunOnTheNodeDefault()
    {
        // Act
        string text = ModelChoice.Describe(null, null);

        // Assert
        text.Should().Be("models: none (no instance/models.json, no node.json claude.models): jobs run on the node default\n");
    }

    // AC-9: --type takes the row's model and sets job_type.
    [Fact]
    public void ForSubmit_Type_RowModelAndJobType()
    {
        // Act
        ModelPick pick = ModelChoice.ForSubmit(Table(), null, "spec", null);

        // Assert
        pick.Should().BeEquivalentTo(new ModelPick(Fable, "spec", "model: claude-fable-5-2 (job type spec)"));
    }

    // AC-9: --model overrides the table for one job, with or without --type.
    [Theory]
    [InlineData("spec", "spec")]
    [InlineData(null, null)]
    public void ForSubmit_ModelGiven_OverrideKeepsTheType(string? type, string? jobType)
    {
        // Act
        ModelPick pick = ModelChoice.ForSubmit(Table(), null, type, Opus);

        // Assert
        pick.Should().BeEquivalentTo(new ModelPick(Opus, jobType, "model: claude-opus-5-5 (override)"));
    }

    // AC-9: neither option: no model, no job type.
    [Fact]
    public void ForSubmit_Neither_NodeDefault()
    {
        // Act
        ModelPick pick = ModelChoice.ForSubmit(Table(), null, null, null);

        // Assert
        pick.Should().BeEquivalentTo(new ModelPick(null, null, "model: node default"));
    }

    // AC-9: without a table, --model is checked against the machine's node.json claude.models.
    [Fact]
    public void ForSubmit_NoTableModelOnTheNodeList_Override()
    {
        // Act
        ModelPick pick = ModelChoice.ForSubmit(null, [Fable], null, Fable);

        // Assert
        pick.Should().BeEquivalentTo(new ModelPick(Fable, null, "model: claude-fable-5-2 (override)"));
    }

    // AC-9: each refusal (exit 2 in slice 6) names its option and lists what is allowed; nothing is chosen.
    [Theory]
    [InlineData("testing", null, "type", "type testing is not in the model table; types: spec, implementation")]
    [InlineData("Spec", null, "type", "type is not a job type label (a–z 0–9 -, 1–63)")]
    [InlineData(null, "claude-sonnet-5-5", "model", "model claude-sonnet-5-5 is not on the allow-list (instance/models.json models): claude-opus-5-5, claude-fable-5-2")]
    [InlineData(null, "sonnet", "model", "model " + JobModelRules.ModelRule)]
    public void ForSubmit_Refused_NamesTheOptionAndTheList(string? type, string? model, string field, string detail)
    {
        // Act
        ModelPick pick = ModelChoice.ForSubmit(Table(), null, type, model);

        // Assert
        pick.Should().BeEquivalentTo(new ModelPick(null, null, null, new ModelPickError(field, detail)));
    }

    // AC-9: no table and no node list: --model has no allow-list, --type no table.
    [Theory]
    [InlineData(null, Opus, "model", "--model needs an allow-list: instance/models.json models or node.json claude.models")]
    [InlineData("spec", null, "type", "--type needs a model table: no instance/models.json on this machine")]
    public void ForSubmit_NoListAtAll_Refused(string? type, string? model, string field, string detail)
    {
        // Act
        ModelPick pick = ModelChoice.ForSubmit(null, null, type, model);

        // Assert
        pick.Error.Should().Be(new ModelPickError(field, detail));
    }

    // AC-9: on a machine without the instance file, the node list refuses an id off it.
    [Fact]
    public void ForSubmit_NoTableModelOffTheNodeList_Refused()
    {
        // Act
        ModelPick pick = ModelChoice.ForSubmit(null, [Fable], null, Opus);

        // Assert
        pick.Error.Should().Be(new ModelPickError("model", "model claude-opus-5-5 is not on the allow-list (node.json claude.models): claude-fable-5-2"));
    }

    // AC-10: a mandate job takes the row of its kind and job_type = kind.
    [Fact]
    public void ForMandate_KindInTheTable_RowModelAndJobType()
    {
        // Arrange
        ModelTable table = Table() with { JobTypes = [new ModelTableRow("build", Opus)] };

        // Act
        ModelPick pick = ModelChoice.ForMandate(table, MandateJobKind.Build);

        // Assert
        pick.Should().BeEquivalentTo(new ModelPick(Opus, "build", "model: claude-opus-5-5 (job type build)"));
    }

    // AC-10: a kind without a row (or no table) runs on the node default, still with job_type = kind.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ForMandate_NoRowForTheKind_NoModelJobTypeKept(bool withTable)
    {
        // Act
        ModelPick pick = ModelChoice.ForMandate(withTable ? Table() : null, MandateJobKind.Verify);

        // Assert
        pick.Should().BeEquivalentTo(new ModelPick(null, "verify", "model: node default"));
    }

    private static ModelTable Table() => ModelTable.Parse(Example).Table!;
}
