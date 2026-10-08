using System.Text.Json;

using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Zyggy.Core.Dream;
using Zyggy.Core.Models;
using Zyggy.Core.Runs;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Dream;

/// <summary>AC-13 (unit, on the request): the filing call's isolation, and how its result ends the batch.</summary>
public sealed class DreamFilerTests : IDisposable
{
    private static readonly DateOnly RunDate = new(2026, 10, 4);

    private readonly MemoryTree _tree = new(
        ("private/people/_index.md", "---\nname: people\ndescription: People\nupdated: 2026-09-28\n---\n"),
        ("private/people/carol.md", "---\nname: carol\ndescription: Carol\nupdated: 2026-09-28\n---\n- [stated] 2026-09-28: Carol is my sister.\n"),
        ("inbox/remember-2026-09-29.md", "- [stated] 2026-09-29: Carol likes tea.\n"));

    private readonly IModelRunner _model = Substitute.For<IModelRunner>();
    private ModelRunRequest? _request;
    private string? _runDirectoryDuringCall;

    public void Dispose() => _tree.Dispose();

    private string StateDirectory => Path.Combine(_tree.Root, "state");

    private DreamRunContext Context(DreamOptions? options = null) =>
        new(_tree.Paths, StateDirectory, RunDate, options ?? new DreamOptions());

    private void Returns(ModelRunResult result) =>
        _model.RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            _request = call.Arg<ModelRunRequest>();
            _runDirectoryDuringCall = Directory.Exists(_request.WorkingDirectory)
                ? string.Join(",", Directory.EnumerateFileSystemEntries(_request.WorkingDirectory))
                : null;
            return result;
        });

    private static JsonElement ValidProposal() =>
        TestProposals.New()
            .Disposition("L1", "filed", "private/people/carol.md")
            .Edit("private/people/carol.md", append: ["- [stated] 2026-09-29: Carol likes tea."])
            .Build();

    private async Task<BatchOutcome> FileAsync(DreamOptions? options = null)
    {
        var snapshot = MemorySnapshot.Load(_tree.Paths);
        var batch = BatchPlanner.Plan(snapshot, DreamLedger.Empty(), new HashSet<string>(), 150, 60_000)!;
        var filer = new DreamFiler(_model, new DreamPrompts(), new FakeTimeProvider());
        return await filer.FileBatchAsync(batch, snapshot, new WorkingSet(snapshot), Context(options), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FileBatch_Request_WorkingDirectoryIsEmptyRunDirAndPrincipalIsAddDir()
    {
        // Arrange
        Returns(TestModelResults.Succeeded(ValidProposal()));

        // Act
        await FileAsync();

        // Assert
        Path.GetDirectoryName(_request!.WorkingDirectory).Should().Be(Path.Combine(StateDirectory, "runs"));
        _runDirectoryDuringCall.Should().BeEmpty("the run directory exists and is empty during the call");
        _request.AdditionalDirectories.Should().Equal(_tree.PrincipalDirectory);
    }

    [Fact]
    public async Task FileBatch_Request_ToolsReadGrepGlobOnly()
    {
        // Arrange
        Returns(TestModelResults.Succeeded(ValidProposal()));

        // Act
        await FileAsync();

        // Assert
        _request!.Tools.Should().Equal("Read", "Grep", "Glob");
        _request.AllowedTools.Should().BeEmpty();
    }

    [Fact]
    public async Task Request_DisallowsLinkedInToolAndVerbs_NoMcp()
    {
        // Arrange
        Returns(TestModelResults.Succeeded(ValidProposal()));

        // Act
        await FileAsync();

        // Assert: spec 36 AC-8 — one deny list led by mcp__*, then the linkedin tool and verbs; no MCP server loaded
        _request!.DisallowedTools.Should().Equal("mcp__linkedin__*", "Bash(zyggy linkedin *)");
        global::Zyggy.Core.Models.ClaudeArguments.Build(_request).Should().ContainInConsecutiveOrder("--disallowedTools", "mcp__*,mcp__linkedin__*,Bash(zyggy linkedin *)").And.Contain("--strict-mcp-config").And.NotContain("--mcp-config");
    }

    [Fact]
    public async Task FileBatch_Request_IsolationNoMcpNoHooksNoAutoMemoryNoSlashCommands()
    {
        // Arrange
        Returns(TestModelResults.Succeeded(ValidProposal()));

        // Act
        await FileAsync();

        // Assert
        _request!.Isolation.Should().Be(ModelSessionIsolation.NoMcp | ModelSessionIsolation.NoHooks | ModelSessionIsolation.NoAutoMemory
            | ModelSessionIsolation.NoSlashCommands);
    }

    [Fact]
    public async Task FileBatch_Request_EnvironmentIsOnlyZyggyHooksOff()
    {
        // Arrange
        Returns(TestModelResults.Succeeded(ValidProposal()));

        // Act
        await FileAsync();

        // Assert
        _request!.Environment.Should().Equal(new Dictionary<string, string> { ["ZYGGY_HOOKS"] = "off" });
    }

    [Fact]
    public async Task FileBatch_Request_JsonSchemaIsFilingSchemaAndTranscriptNull()
    {
        // Arrange
        Returns(TestModelResults.Succeeded(ValidProposal()));
        var prompts = new DreamPrompts();

        // Act
        await FileAsync();

        // Assert
        _request!.JsonSchema.Should().Be(prompts.FilingSchema);
        _request.AppendSystemPrompt.Should().Be(prompts.FilingPrompt);
        _request.TranscriptPath.Should().BeNull();
        _request.Prompt.Should().Contain("L1 inbox/remember-2026-09-29.md: - [stated] 2026-09-29: Carol likes tea.");
    }

    [Fact]
    public async Task FileBatch_Request_TurnsBudgetTimeoutFromOptions()
    {
        // Arrange
        Returns(TestModelResults.Succeeded(ValidProposal()));

        // Act
        await FileAsync(new DreamOptions { CallMaxTurns = 12, CallMaxBudgetUsd = 2.5m, CallTimeoutMinutes = 7, Model = "sonnet" });

        // Assert
        _request!.MaxTurns.Should().Be(12);
        _request.MaxBudgetUsd.Should().Be(2.5m);
        _request.Timeout.Should().Be(TimeSpan.FromMinutes(7));
        _request.Model.Should().Be("sonnet");
    }

    [Fact]
    public async Task FileBatch_ModelFailed_ReturnsFailedWithReasonAndDetail()
    {
        // Arrange
        Returns(TestModelResults.Failed(RunFailureReason.Timeout, "timeout"));

        // Act
        var outcome = await FileAsync();

        // Assert
        outcome.Should().BeOfType<BatchFailed>().Which.Should().Match<BatchFailed>(f => f.Reason == RunFailureReason.Timeout && f.Detail == "timeout");
    }

    [Theory]
    [InlineData("""{"dispositions": "nope"}""")]
    [InlineData("""{"dispositions": [], "new_categories": [], "creates": [], "edits": []}""")]
    [InlineData("""{"dispositions": [], "new_categories": [], "creates": [], "edits": [], "notes": "", "extra": 1}""")]
    [InlineData("""[1, 2]""")]
    public async Task FileBatch_StructuredOutputNotDeserialisable_AbortsFormatInvalid(string json)
    {
        // Arrange
        Returns(TestModelResults.Succeeded(JsonDocument.Parse(json).RootElement.Clone()));

        // Act
        var outcome = await FileAsync();

        // Assert
        outcome.Should().BeOfType<BatchAborted>().Which.Check.Should().Be(DreamCheck.FormatInvalid);
    }

    [Fact]
    public async Task FileBatch_Accepted_RunDirDeleted()
    {
        // Arrange
        Returns(TestModelResults.Succeeded(ValidProposal(), cost: 0.4m));

        // Act
        var outcome = await FileAsync();

        // Assert
        var accepted = outcome.Should().BeOfType<BatchAccepted>().Subject;
        accepted.CostUsd.Should().Be(0.4m);
        accepted.Counts.Lines.Should().Be(1);
        accepted.Counts.Filed.Should().Be(1);
        accepted.Counts.FilesEdited.Should().Be(1);
        Directory.Exists(_request!.WorkingDirectory).Should().BeFalse();
    }
}
