using Zyggy.Core.Supervision;

namespace Zyggy.Core.Tests.Supervision;

// Spec 50 slice 5, AC-11 (unit): the supervisor run's model comes from `instance/models.json` `supervisor` in Central's own project
// (supervision.project, Q7); absent or null is today's run; an invalid table starts no run.
public sealed class SupervisorWakeModelTests
{
    private const string Opus = "claude-opus-5-5";

    private static readonly string Project = Path.Join(Path.GetTempPath(), "central-desk");

    private static readonly string TableFile = Path.Join(Project, "instance", "models.json");

    // AC-11: the supervisor row is the run's model.
    [Fact]
    public void For_SupervisorSet_ItsModel()
    {
        // Act
        SupervisorModel model = SupervisorModel.For(Project, File($$"""{ "models": ["{{Opus}}"], "supervisor": "{{Opus}}" }"""));

        // Assert
        model.Should().Be(new SupervisorModel(Opus, null));
    }

    // AC-11: null supervisor, or no file at all: today's run.
    [Theory]
    [InlineData("""{ "models": ["claude-opus-5-5"], "supervisor": null }""")]
    [InlineData("""{ "models": ["claude-opus-5-5"] }""")]
    [InlineData(null)]
    public void For_NoSupervisorModel_TodaysRun(string? json)
    {
        // Act
        SupervisorModel model = SupervisorModel.For(Project, File(json));

        // Assert
        model.Should().Be(new SupervisorModel(null, null));
    }

    // AC-11: a supervisor off the allow-list makes the table invalid: no model, the error that the wake logs once.
    [Fact]
    public void For_SupervisorOffTheList_ErrorNoModel()
    {
        // Act
        SupervisorModel model = SupervisorModel.For(Project, File("""{ "models": ["claude-opus-5-5"], "supervisor": "claude-fable-5-2" }"""));

        // Assert
        model.Should().Be(new SupervisorModel(null, "instance/models.json: supervisor model claude-fable-5-2 not allowed: not on models"));
    }

    private static Func<string, string?> File(string? json) => path => path == TableFile ? json : throw new InvalidOperationException("read " + path);
}
