using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Zyggy.Core.Models;
using Zyggy.Core.Processes;
using Zyggy.Core.Runs;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Models;

/// <summary>
/// Spec 50 slice 3: a run that must run on its model never falls back. The <c>system</c> event naming another model stops the process
/// before any further line (AC-4); Claude Code's own refusal of the model, as captured in <c>tools/fake-claude/scenarios/model-refused.jsonl</c>
/// (Q5), is <c>model_unavailable</c>, not <c>claude_error</c> (AC-5). A run that does not require its model is untouched.
/// </summary>
public sealed class ClaudeCodeCliRunnerModelTests
{
    private const string Opus = "claude-opus-5-5";

    private readonly IProcessRunner _processes = Substitute.For<IProcessRunner>();

    private int _linesDelivered;

    // AC-4: another model in the system event: the runner cancels the process (the real runner kills the tree and throws), no line after it is read.
    [Fact]
    public async Task RunAsync_SystemEventNamesAnotherModel_StoppedAtOnceModelUnavailableMismatch()
    {
        // Arrange
        Script(StreamLines.Init("claude-sonnet-5-5"), StreamLines.Assistant(), StreamLines.Result());

        // Act
        ModelRunResult result = await Runner().RunAsync(Required(Opus), TestContext.Current.CancellationToken);

        // Assert
        new { result.Outcome, result.Reason, result.FailureDetail, result.Model, Lines = _linesDelivered }.Should().BeEquivalentTo(new
        {
            Outcome = ModelRunOutcome.Failed,
            Reason = (RunFailureReason?)RunFailureReason.ModelUnavailable,
            FailureDetail = ModelFailureDetail.ModelMismatch,
            Model = "claude-sonnet-5-5",
            Lines = 1,
        });
    }

    // AC-4: equal, or equal followed by one [label] suffix, runs on.
    [Theory]
    [InlineData("claude-opus-5-5")]
    [InlineData("claude-opus-5-5[1m]")]
    public async Task RunAsync_SystemEventNamesTheAskedModel_RunsOn(string reported)
    {
        // Arrange
        Script(StreamLines.Init(reported), StreamLines.Assistant(), StreamLines.Result());

        // Act
        ModelRunResult result = await Runner().RunAsync(Required(Opus), TestContext.Current.CancellationToken);

        // Assert
        new { result.Outcome, Lines = _linesDelivered }.Should().BeEquivalentTo(new { Outcome = ModelRunOutcome.Succeeded, Lines = 3 });
    }

    // Spec 50 "A job without model is never compared": the dream, brief and m365 runs name aliases and keep today's behaviour.
    [Fact]
    public async Task RunAsync_ModelNotRequired_NeverCompared()
    {
        // Arrange
        Script(StreamLines.Init("claude-sonnet-5-5"), StreamLines.Result());

        // Act
        ModelRunResult result = await Runner().RunAsync(Request() with { Model = "opus" }, TestContext.Current.CancellationToken);

        // Assert
        result.Outcome.Should().Be(ModelRunOutcome.Succeeded);
    }

    // AC-5 on the captured stream (Q5): exit 1, an assistant event with error model_not_found, a result with is_error true.
    [Fact]
    public async Task RunAsync_CapturedModelRefusal_ModelUnavailableRefused()
    {
        // Arrange
        Script(1, CapturedRefusal());

        // Act
        ModelRunResult result = await Runner().RunAsync(Required("claude-opus-0-0"), TestContext.Current.CancellationToken);

        // Assert
        new { result.Outcome, result.Reason, result.FailureDetail, result.ResultText }.Should().BeEquivalentTo(new
        {
            Outcome = ModelRunOutcome.Failed,
            Reason = (RunFailureReason?)RunFailureReason.ModelUnavailable,
            FailureDetail = ModelFailureDetail.ModelRefused,
            ResultText = "There's an issue with the selected model (claude-opus-0-0). It may not exist or you may not have access to it. Run --model to pick a different model.",
        });
    }

    // The same stream for a run that does not require its model stays claude_error, as today.
    [Fact]
    public async Task RunAsync_CapturedModelRefusalModelNotRequired_ClaudeErrorAsToday()
    {
        // Arrange
        Script(1, CapturedRefusal());

        // Act
        ModelRunResult result = await Runner().RunAsync(Request() with { Model = "claude-opus-0-0" }, TestContext.Current.CancellationToken);

        // Assert
        result.Reason.Should().Be(RunFailureReason.ClaudeError);
    }

    // Another error of a required-model run (here: a usage limit) is not mistaken for a refusal of the model.
    [Fact]
    public async Task RunAsync_RequiredModelOtherError_ClaudeError()
    {
        // Arrange
        Script(StreamLines.Init(Opus), StreamLines.Result(isError: true, resultText: "usage limit reached"));

        // Act
        ModelRunResult result = await Runner().RunAsync(Required(Opus), TestContext.Current.CancellationToken);

        // Assert
        new { result.Reason, result.FailureDetail }.Should().BeEquivalentTo(new { Reason = (RunFailureReason?)RunFailureReason.ClaudeError, FailureDetail = ModelFailureDetail.RateLimit });
    }

    private static ModelRunRequest Request() => new("the prompt", "/run/dir", TimeSpan.FromMinutes(15));

    private static ModelRunRequest Required(string model) => Request() with { Model = model, RequireModel = true };

    private static string[] CapturedRefusal() =>
        File.ReadAllText(Path.Combine(RepoRoot.Find(), "tools", "fake-claude", "scenarios", "model-refused.jsonl"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private ClaudeCodeCliRunner Runner() =>
        new(_processes, Options.Create(new ClaudeCodeOptions { Path = "claude" }), new FakeTimeProvider());

    private void Script(params string[] lines) => Script(0, lines);

    // Like the real process runner: a cancelled token kills the process, so no later line arrives, and the call throws.
    private void Script(int exitCode, params string[] lines) =>
        _processes.RunAsync(Arg.Any<ProcessSpec>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var spec = call.Arg<ProcessSpec>();
            var token = call.Arg<CancellationToken>();
            foreach (var line in lines)
            {
                token.ThrowIfCancellationRequested();
                _linesDelivered++;
                spec.OnStdoutLine?.Invoke(line);
            }

            token.ThrowIfCancellationRequested();
            return new ProcessResult(exitCode, Stdout: "", Stderr: "", TimedOut: false, StartFailed: false, StdoutTruncated: false, Duration: TimeSpan.FromSeconds(2));
        });
}
