using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using Zyggy.Core.Models;
using Zyggy.Core.Processes;
using Zyggy.Core.Runs;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Models;

public sealed class ClaudeCodeCliRunnerTests
{
    private static readonly string MissingRootedClaude = Path.Combine(Path.GetTempPath(), "zyggy-no-such-dir", "claude");

    private readonly IProcessRunner _processes = Substitute.For<IProcessRunner>();
    private ProcessSpec? _captured;

    private static ModelRunRequest Request() => new("the prompt", "/run/dir", TimeSpan.FromMinutes(15));

    private static ProcessResult Exited(int code = 0) =>
        new(code, Stdout: "", Stderr: "", TimedOut: false, StartFailed: false, StdoutTruncated: false, Duration: TimeSpan.FromSeconds(2));

    private ClaudeCodeCliRunner Runner(string path = "claude") =>
        new(_processes, Options.Create(new ClaudeCodeOptions { Path = path }), new FakeTimeProvider());

    private void Script(ProcessResult result, params string[] lines) =>
        _processes.RunAsync(Arg.Any<ProcessSpec>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var spec = call.Arg<ProcessSpec>();
            _captured = spec;
            foreach (var line in lines)
            {
                spec.OnStdoutLine?.Invoke(line);
            }

            return result;
        });

    [Fact]
    public async Task RunAsync_Request_SpecHasClaudePathWorkingDirectoryPromptOnStdinAndArguments()
    {
        // Arrange
        Script(Exited(), StreamLines.Init(), StreamLines.Result());
        var request = Request() with { Tools = ["Read"] };

        // Act
        await Runner("/opt/claude/claude").RunAsync(request, TestContext.Current.CancellationToken);

        // Assert
        _captured!.FileName.Should().Be("/opt/claude/claude");
        _captured.WorkingDirectory.Should().Be("/run/dir");
        _captured.StandardInput.Should().Be("the prompt");
        _captured.Arguments.Should().Equal(ClaudeArguments.Build(request));
        _captured.Timeout.Should().Be(TimeSpan.FromMinutes(15));
        _captured.MaxStdoutBytes.Should().Be(request.MaxCaptureBytes);
        _captured.OnStdoutLine.Should().NotBeNull();
    }

    [Fact]
    public async Task RunAsync_Request_EnvironmentAddsRequestVariablesAndRemovesCredentialsDirectory()
    {
        // Arrange
        Script(Exited(), StreamLines.Result());
        var request = Request() with { Environment = new Dictionary<string, string> { ["ZYGGY_HOOKS"] = "off" } };

        // Act
        await Runner().RunAsync(request, TestContext.Current.CancellationToken);

        // Assert
        _captured!.Environment.Should().Contain("ZYGGY_HOOKS", "off");
        _captured.Environment.Should().ContainKey("CREDENTIALS_DIRECTORY").WhoseValue.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_RequestNamesCredentialsDirectory_PassedThrough()
    {
        // Arrange: the m365 runs pass it on purpose — Claude Code's headersHelper reads the unit's key copy (D8)
        Script(Exited(), StreamLines.Result());
        var request = Request() with { Environment = new Dictionary<string, string> { ["CREDENTIALS_DIRECTORY"] = "/run/credentials/unit" } };

        // Act
        await Runner().RunAsync(request, TestContext.Current.CancellationToken);

        // Assert
        _captured!.Environment.Should().Contain("CREDENTIALS_DIRECTORY", "/run/credentials/unit");
    }

    [Fact]
    public async Task RunAsync_SuccessWithSchema_ReturnsSucceededWithStructuredOutput()
    {
        // Arrange
        Script(Exited(), StreamLines.Init("fake-model-1"), StreamLines.Result(structuredOutputJson: """{"ok":true}""", permissionDenials: 1));

        // Act
        var result = await Runner().RunAsync(Request() with { JsonSchema = "{}" }, TestContext.Current.CancellationToken);

        // Assert
        result.Outcome.Should().Be(ModelRunOutcome.Succeeded);
        result.Reason.Should().BeNull();
        result.FailureDetail.Should().BeNull();
        result.StructuredOutput!.Value.GetProperty("ok").GetBoolean().Should().BeTrue();
        result.CostUsd.Should().Be(0.0123m);
        result.NumTurns.Should().Be(2);
        result.Model.Should().Be("fake-model-1");
        result.InputTokens.Should().Be(100);
        result.OutputTokens.Should().Be(20);
        result.ExitCode.Should().Be(0);
        result.ResultText.Should().Be("done");
        result.PermissionDenials.Should().Be(1);
        result.Duration.Should().Be(TimeSpan.FromSeconds(2));
    }

    public sealed record FailureCase(
        string Name,
        ProcessResult Process,
        string[] Lines,
        bool WithSchema,
        int MaxCaptureBytes,
        string Path,
        RunFailureReason Reason,
        string Detail)
    {
        public override string ToString() => Name;
    }

    public static TheoryData<FailureCase> FailureShapes()
    {
        var ok = Exited();
        var start = ok with { ExitCode = null, StartFailed = true };
        var timedOut = ok with { ExitCode = null, TimedOut = true };
        string[] success = [StreamLines.Init(), StreamLines.Result()];
        const int Cap = 2 * 1024 * 1024;
        return
        [
            new FailureCase("start failed, rooted path missing", start, [], false, Cap, MissingRootedClaude, RunFailureReason.ClaudeError, "not_found"),
            new FailureCase("start failed otherwise", start, [], false, Cap, "claude", RunFailureReason.ClaudeError, "start_failed"),
            new FailureCase("timed out", timedOut, [StreamLines.Init()], false, Cap, "claude", RunFailureReason.Timeout, "timeout"),
            new FailureCase("exit 1 with success result", Exited(1), success, false, Cap, "claude", RunFailureReason.ClaudeError, "exit_1"),
            new FailureCase("is_error unknown subtype", Exited(1), [StreamLines.Result(isError: true, subtype: "error_new_kind")], false, Cap, "claude",
                RunFailureReason.ClaudeError, "is_error"),
            new FailureCase("error_max_turns", Exited(1), [StreamLines.Result(isError: true, subtype: "error_max_turns")], false, Cap, "claude",
                RunFailureReason.ClaudeError, "error_max_turns"),
            new FailureCase("error_max_budget_usd", Exited(1), [StreamLines.Result(isError: true, subtype: "error_max_budget_usd")], false, Cap, "claude",
                RunFailureReason.ClaudeError, "error_max_budget_usd"),
            new FailureCase("error_max_structured_output_retries", Exited(1),
                [StreamLines.Result(isError: true, subtype: "error_max_structured_output_retries")], true, Cap, "claude",
                RunFailureReason.ClaudeError, "error_max_structured_output_retries"),
            new FailureCase("error_during_execution", Exited(1), [StreamLines.Result(isError: true, subtype: "error_during_execution")], false, Cap, "claude",
                RunFailureReason.ClaudeError, "error_during_execution"),
            new FailureCase("no result event", ok, [StreamLines.Init(), StreamLines.Assistant()], false, Cap, "claude", RunFailureReason.ClaudeError, "no_result"),
            new FailureCase("unparseable result", ok, ["""{"type":"result","subtype":"success", broken"""], false, Cap, "claude",
                RunFailureReason.ClaudeError, "unparseable_result"),
            new FailureCase("schema without structured output", ok, success, true, Cap, "claude", RunFailureReason.ClaudeError, "no_structured_output"),
            new FailureCase("output over the cap", ok, success, false, 64, "claude", RunFailureReason.ClaudeError, "output_too_large"),
            new FailureCase("stdout truncated by the process runner", ok with { StdoutTruncated = true }, success, false, Cap, "claude",
                RunFailureReason.ClaudeError, "output_too_large"),
            new FailureCase("auth marker", Exited(1), [StreamLines.Result(isError: true, resultText: "Invalid API key · Please run /login")], false, Cap,
                "claude", RunFailureReason.ClaudeError, "auth"),
            new FailureCase("rate marker", Exited(1), [StreamLines.Result(isError: true, resultText: "Claude AI usage limit reached|1759550400")], false,
                Cap, "claude", RunFailureReason.ClaudeError, "rate_limit"),
        ];
    }

    [Theory]
    [MemberData(nameof(FailureShapes))]
    public async Task RunAsync_FailureShape_ReturnsFailedWithReasonAndDetail(FailureCase shape)
    {
        // Arrange
        Script(shape.Process, shape.Lines);
        var request = Request() with { JsonSchema = shape.WithSchema ? "{}" : null, MaxCaptureBytes = shape.MaxCaptureBytes };

        // Act
        var result = await Runner(shape.Path).RunAsync(request, TestContext.Current.CancellationToken);

        // Assert
        result.Outcome.Should().Be(ModelRunOutcome.Failed);
        result.Reason.Should().Be(shape.Reason);
        result.FailureDetail.Should().Be(shape.Detail);
    }

    [Fact]
    public async Task RunAsync_ProcessRunnerThrowsUnexpected_ReturnsClaudeErrorNeverThrows()
    {
        // Arrange
        _processes.RunAsync(Arg.Any<ProcessSpec>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("boom"));

        // Act
        var result = await Runner().RunAsync(Request(), TestContext.Current.CancellationToken);

        // Assert
        result.Outcome.Should().Be(ModelRunOutcome.Failed);
        result.Reason.Should().Be(RunFailureReason.ClaudeError);
        result.FailureDetail.Should().Be("start_failed");
    }

    [Fact]
    public async Task RunAsync_CallerCancels_ThrowsOperationCanceled()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        _processes.RunAsync(Arg.Any<ProcessSpec>(), Arg.Any<CancellationToken>()).Returns<Task<ProcessResult>>(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        // Act
        Func<Task> act = () => Runner().RunAsync(Request(), cts.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task RunAsync_NullRequest_ThrowsArgumentNull()
    {
        // Act
        Func<Task> act = () => Runner().RunAsync(null!, TestContext.Current.CancellationToken);

        // Assert
        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
