using System.Text;

using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Models;
using Zyggy.Core.Processes;
using Zyggy.Core.Runs;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Models;

public sealed class ClaudeCodeCliRunnerTests : IDisposable
{
    private readonly ScratchDirectory _scratch = new();
    private ServiceProvider? _provider;

    public void Dispose()
    {
        _provider?.Dispose();
        _scratch.Dispose();
    }

    private IModelRunner Runner(string? claudePath = null)
    {
        _provider = new ServiceCollection()
            .AddProcessRunner()
            .AddClaudeCodeModelRunner(o => o.Path = claudePath ?? FakeClaude.ExecutablePath)
            .BuildServiceProvider();
        return _provider.GetRequiredService<IModelRunner>();
    }

    private ModelRunRequest Request(Dictionary<string, string> env, int seconds = 30) =>
        new("Prompt from the test.", _scratch.Path, TimeSpan.FromSeconds(seconds)) { Environment = env };

    [Fact]
    public async Task RunAsync_SuccessScenario_ReturnsSucceededWithStructuredOutputCostTurnsAndModel()
    {
        // Arrange
        var request = Request(new() { ["ZYGGY_FAKE_CLAUDE_SCENARIO"] = "success-structured" }) with { JsonSchema = """{"type":"object"}""" };

        // Act
        var result = await Runner().RunAsync(request, TestContext.Current.CancellationToken);

        // Assert
        result.Outcome.Should().Be(ModelRunOutcome.Succeeded);
        result.StructuredOutput!.Value.GetProperty("ok").GetBoolean().Should().BeTrue();
        result.CostUsd.Should().Be(0.0123m);
        result.NumTurns.Should().Be(2);
        result.Model.Should().Be("fake-model-1");
        result.ExitCode.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_ErrorScenarioExitOne_ReturnsClaudeErrorIsError()
    {
        // Arrange
        var request = Request(new() { ["ZYGGY_FAKE_CLAUDE_SCENARIO"] = "error", ["ZYGGY_FAKE_CLAUDE_EXIT"] = "1" });

        // Act
        var result = await Runner().RunAsync(request, TestContext.Current.CancellationToken);

        // Assert
        result.Outcome.Should().Be(ModelRunOutcome.Failed);
        result.Reason.Should().Be(RunFailureReason.ClaudeError);
        result.FailureDetail.Should().Be("error_during_execution");
        result.ExitCode.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_DelayBeyondTimeout_ReturnsTimeoutAndLeavesNoProcess()
    {
        // Arrange
        var request = Request(new() { ["ZYGGY_FAKE_CLAUDE_DELAY_MS"] = "30000" }, seconds: 2);

        // Act
        var result = await Runner().RunAsync(request, TestContext.Current.CancellationToken);

        // Assert
        result.Outcome.Should().Be(ModelRunOutcome.Failed);
        result.Reason.Should().Be(RunFailureReason.Timeout);
        result.FailureDetail.Should().Be("timeout");
        ProcessProbe.ProcessesWithWorkingDirectory(_scratch.Path).Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_DreamShapedRequest_CapturedArgumentsAndStdinMatchContract()
    {
        // Arrange
        var argsPath = Path.Combine(Path.GetTempPath(), "zyggy-it", Guid.NewGuid().ToString("N") + "-args.bin");
        var stdinPath = Path.Combine(Path.GetTempPath(), "zyggy-it", Guid.NewGuid().ToString("N") + "-stdin.bin");
        const string Prompt = "BATCH L1 [stated] likes tea\n";
        var request = new ModelRunRequest(Prompt, _scratch.Path, TimeSpan.FromSeconds(30))
        {
            Environment = new Dictionary<string, string>
            {
                ["ZYGGY_FAKE_CLAUDE_SCENARIO"] = "success-structured",
                ["ZYGGY_FAKE_CLAUDE_CAPTURE"] = argsPath,
                ["ZYGGY_FAKE_CLAUDE_STDIN_CAPTURE"] = stdinPath,
                ["ZYGGY_HOOKS"] = "off",
            },
            Tools = ["Read", "Grep", "Glob"],
            AdditionalDirectories = ["/srv/memory/acme/alice"],
            MaxTurns = 20,
            MaxBudgetUsd = 5m,
            JsonSchema = """{"type":"object"}""",
            AppendSystemPrompt = "File the facts.",
            Isolation = ModelSessionIsolation.NoMcp | ModelSessionIsolation.NoHooks | ModelSessionIsolation.NoAutoMemory
                | ModelSessionIsolation.NoSlashCommands,
        };
        string[] expected =
        [
            "-p", "--output-format", "stream-json", "--verbose", "--permission-mode", "auto", "--permission-prompts", "none",
            "--no-session-persistence", "--max-turns", "20", "--max-budget-usd", "5", "--tools", "Read,Grep,Glob",
            "--add-dir", "/srv/memory/acme/alice", "--json-schema", """{"type":"object"}""", "--append-system-prompt", "File the facts.",
            "--strict-mcp-config", "--disallowedTools", "mcp__*", "--settings", """{"disableAllHooks":true,"autoMemoryEnabled":false}""",
            "--disable-slash-commands",
        ];

        try
        {
            // Act
            var result = await Runner().RunAsync(request, TestContext.Current.CancellationToken);

            // Assert
            result.Outcome.Should().Be(ModelRunOutcome.Succeeded);
            var capture = FakeClaude.ReadCapture(argsPath);
            capture.Arguments.Should().Equal(expected);
            Path.GetFullPath(capture.WorkingDirectory).Should().Be(Path.GetFullPath(_scratch.Path));
            File.ReadAllBytes(stdinPath).Should().Equal(new UTF8Encoding(false).GetBytes(Prompt));
        }
        finally
        {
            File.Delete(argsPath);
            File.Delete(stdinPath);
        }
    }

    [Fact]
    public async Task RunAsync_ClaudePathMissing_ReturnsClaudeErrorNotFound()
    {
        // Arrange
        var request = Request([]);

        // Act
        var result = await Runner(_scratch.File("claude-missing")).RunAsync(request, TestContext.Current.CancellationToken);

        // Assert
        result.Outcome.Should().Be(ModelRunOutcome.Failed);
        result.Reason.Should().Be(RunFailureReason.ClaudeError);
        result.FailureDetail.Should().Be("not_found");
    }
}
