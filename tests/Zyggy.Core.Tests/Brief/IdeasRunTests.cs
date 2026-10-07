using System.Text.Json;

using NSubstitute;

using Zyggy.Core.Brief;
using Zyggy.Core.Models;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Brief;

/// <summary>
/// Spec 35 AC-32, AC-46: the ideas run in the dream's read-only shape — an empty working directory under <c>brief/runs/</c>, Read, Grep
/// and Glob only, the principal's memory as an added directory, no MCP, hooks, auto memory or slash commands, the forbidden folders denied.
/// </summary>
public sealed class IdeasRunTests : IDisposable
{
    private readonly BriefFixture _fixture = new();
    private readonly MemoryTree _tree = new();
    private readonly IModelRunner _model = Substitute.For<IModelRunner>();
    private readonly BriefPrompts _prompts = new();

    public void Dispose()
    {
        _tree.Dispose();
        _fixture.Dispose();
    }

    private IdeasRun Run() => new(_model, _prompts, _fixture.Paths, _tree.Paths);

    private static ModelRunResult Answer(string json, decimal cost = 0.20m, int turns = 5) =>
        new(ModelRunOutcome.Succeeded, null, null, null, JsonDocument.Parse(json).RootElement.Clone(), cost, turns, TimeSpan.FromSeconds(1), null, null, null, 0, 0);

    private ModelRequestCapture Capture()
    {
        var capture = new ModelRequestCapture();
        _model.RunAsync(Arg.Do<ModelRunRequest>(r =>
        {
            capture.Request = r;
            capture.DirectoryExisted = Directory.Exists(r.WorkingDirectory);
            capture.DirectoryEmpty = capture.DirectoryExisted && !Directory.EnumerateFileSystemEntries(r.WorkingDirectory).Any();
        }), Arg.Any<CancellationToken>()).Returns(Answer("""{"suggestions":[]}"""));
        return capture;
    }

    [Fact]
    public async Task Request_ArgumentVectorEqualsGolden()
    {
        // Arrange
        var capture = Capture();

        // Act
        await Run().RunAsync("the input", 20, 1.0m, null, CancellationToken.None);

        // Assert
        var request = capture.Request!;
        var principal = _tree.Paths.PrincipalDirectory;
        var expected = File.ReadAllLines(BriefFixture.Golden("ideas-args.txt"))
            .Select(l => l
                .Replace("<principal-rule>", principal.Replace('\\', '/').TrimStart('/'), StringComparison.Ordinal)
                .Replace("<principal>", principal, StringComparison.Ordinal)
                .Replace("<schema>", _prompts.IdeasSchema, StringComparison.Ordinal)
                .Replace("<prompt>", _prompts.IdeasPrompt, StringComparison.Ordinal));
        ClaudeArguments.Build(request).Should().Equal(expected);
        request.Prompt.Should().Be("the input");
    }

    [Fact]
    public async Task Request_NoMcpHooksAutoMemorySlashCommands_HooksOffEnv()
    {
        // Arrange
        var capture = Capture();

        // Act
        await Run().RunAsync("x", 20, 1.0m, "sonnet", CancellationToken.None);

        // Assert
        var request = capture.Request!;
        request.Isolation.Should().Be(ModelSessionIsolation.NoMcp | ModelSessionIsolation.NoHooks | ModelSessionIsolation.NoAutoMemory | ModelSessionIsolation.NoSlashCommands);
        request.Environment.Should().Equal(new Dictionary<string, string> { ["ZYGGY_HOOKS"] = "off" });
        request.Tools.Should().Equal("Read", "Grep", "Glob");
        request.AdditionalDirectories.Should().Equal(_tree.Paths.PrincipalDirectory);
        request.Timeout.Should().Be(TimeSpan.FromMinutes(10));
        request.TranscriptPath.Should().BeNull();
        request.McpConfig.Should().BeNull();
        request.Model.Should().Be("sonnet");
    }

    [Fact]
    public async Task Request_RunDirEmptyAndRemovedAfter()
    {
        // Arrange
        var capture = Capture();

        // Act
        await Run().RunAsync("x", 20, 1.0m, null, CancellationToken.None);

        // Assert
        var directory = capture.Request!.WorkingDirectory;
        Path.GetDirectoryName(directory).Should().Be(_fixture.Paths.RunsDirectory);
        Path.GetFileName(directory).Should().MatchRegex("^[0-9A-HJKMNP-TV-Z]{26}$");
        capture.DirectoryExisted.Should().BeTrue();
        capture.DirectoryEmpty.Should().BeTrue();
        Directory.Exists(directory).Should().BeFalse();
    }

    [Fact]
    public async Task Request_NeverBypassFlag()
    {
        // Arrange
        var capture = Capture();

        // Act
        await Run().RunAsync("x", 20, 1.0m, null, CancellationToken.None);

        // Assert
        var args = ClaudeArguments.Build(capture.Request!);
        args.Should().NotContain(a => a.Contains("dangerously", StringComparison.OrdinalIgnoreCase) || a.Contains("bypass", StringComparison.OrdinalIgnoreCase));
        args.Should().ContainInOrder("--permission-mode", "auto");
    }

    [Fact]
    public async Task Run_ValidAnswer_SuggestionsParsedCostTurns()
    {
        // Arrange
        _model.RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>()).Returns(Answer(BriefFixture.GoldenText("ideas-output-ok.json")));

        // Act
        var result = await Run().RunAsync("x", 20, 1.0m, null, CancellationToken.None);

        // Assert
        result.Failure.Should().BeNull();
        result.Suggestions!.Select(s => s.Id).Should().Equal("acme-renewal-offer", "family-autumn-trip", "architect-role");
        result.Suggestions![0].Should().BeEquivalentTo(new IdeaSuggestion("acme-renewal-offer", "client", "Prepare the Acme renewal offer before the November talks",
            "The renewal talks start in November", [new IdeaBasis("business/clients/acme-corp.md", "Acme renewal talks start in November")], new DateOnly(2026, 10, 30), "an outline of the offer"));
        result.Suggestions![1].Prepare.Should().BeNull();
        result.Cost.Should().Be(0.20m);
        result.Turns.Should().Be(5);
    }

    [Theory]
    [InlineData("""{"suggestions":"none"}""", "invalid output (suggestions is not an array)")]
    [InlineData("""{"suggestions":[{"id":"Bad Id","area":"home","text":"t","whyNow":"w","basis":[{"file":"a.md","line":"l"}],"prepare":null}]}""", "invalid output (suggestion 1: id is not a slug)")]
    [InlineData("""{"suggestions":[{"id":"x","area":"home","text":"t","whyNow":"w","basis":[],"prepare":null}]}""", "invalid output (suggestion 1: basis is empty)")]
    [InlineData("""{"suggestions":[{"id":"x","area":"home","text":"t","whyNow":"w","basis":[{"file":"a.md","line":"l"}],"deadline":"soon","prepare":null}]}""", "invalid output (suggestion 1: deadline is not a date)")]
    public async Task Run_InvalidAnswer_FailureNamesIt(string json, string failure)
    {
        // Arrange
        _model.RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>()).Returns(Answer(json));

        // Act
        var result = await Run().RunAsync("x", 20, 1.0m, null, CancellationToken.None);

        // Assert
        result.Suggestions.Should().BeNull();
        result.Failure.Should().Be(failure);
    }

    [Fact]
    public async Task Run_ModelFailed_FailureDetailCostKept()
    {
        // Arrange
        _model.RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>()).Returns(
            new ModelRunResult(ModelRunOutcome.Failed, Zyggy.Core.Runs.RunFailureReason.ClaudeError, "error_during_execution", null, null, 0.05m, 2, TimeSpan.Zero, null, null, null, 1, 0));

        // Act
        var result = await Run().RunAsync("x", 20, 1.0m, null, CancellationToken.None);

        // Assert
        result.Failure.Should().Be("claude run failed (error_during_execution)");
        result.Cost.Should().Be(0.05m);
        result.Turns.Should().Be(2);
    }

    [Fact]
    public async Task Run_OverTheCap_Failure()
    {
        // Arrange
        _model.RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>()).Returns(Answer("""{"suggestions":[]}""", cost: 1.20m, turns: 7));

        // Act
        var result = await Run().RunAsync("x", 20, 1.0m, null, CancellationToken.None);

        // Assert
        result.Failure.Should().Be("over the cap (cost 1.20 > budget 1.0, turns 7 of 20)");
    }

    private sealed class ModelRequestCapture
    {
        public ModelRunRequest? Request { get; set; }

        public bool DirectoryExisted { get; set; }

        public bool DirectoryEmpty { get; set; }
    }
}
