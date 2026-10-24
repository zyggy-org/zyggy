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

    private ClaudeCodeCliRunner Runner(string path = "claude", RecordingLogger<ClaudeCodeCliRunner>? log = null) =>
        new(_processes, Options.Create(new ClaudeCodeOptions { Path = path }), new FakeTimeProvider(), log);

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

    // ----- plan 07 Step 7: the login probe (spec 07 ex-Q3, A7-4) -----

    [Fact]
    public async Task CheckAuthAsync_ExitZero_True()
    {
        // Arrange
        Script(Exited(0));

        // Act
        bool? ok = await Runner().CheckAuthAsync(TestContext.Current.CancellationToken);

        // Assert
        ok.Should().BeTrue();
    }

    [Fact]
    public async Task CheckAuthAsync_ExitOne_False()
    {
        // Arrange
        Script(Exited(1));

        // Act
        bool? ok = await Runner().CheckAuthAsync(TestContext.Current.CancellationToken);

        // Assert
        ok.Should().BeFalse();
    }

    [Theory]
    [InlineData("start-failed")]
    [InlineData("timed-out")]
    [InlineData("exit-two")]
    public async Task CheckAuthAsync_StartFailedTimedOutOrExitTwo_Null(string shape)
    {
        // Arrange
        Script(shape switch
        {
            "start-failed" => new ProcessResult(null, "", "", TimedOut: false, StartFailed: true, StdoutTruncated: false, Duration: TimeSpan.Zero),
            "timed-out" => new ProcessResult(null, "", "", TimedOut: true, StartFailed: false, StdoutTruncated: false, Duration: TimeSpan.FromSeconds(30)),
            _ => Exited(2),
        });

        // Act
        bool? ok = await Runner().CheckAuthAsync(TestContext.Current.CancellationToken);

        // Assert
        ok.Should().BeNull();
    }

    [Fact]
    public async Task CheckAuthAsync_SpecIsAuthStatusWithThirtySecondTimeoutAndNoCredentialsDirectory()
    {
        // Arrange
        Script(Exited(0));

        // Act
        await Runner("/opt/claude/claude").CheckAuthAsync(TestContext.Current.CancellationToken);

        // Assert
        _captured!.FileName.Should().Be("/opt/claude/claude");
        _captured.Arguments.Should().Equal("auth", "status");
        _captured.Timeout.Should().Be(TimeSpan.FromSeconds(30));
        _captured.Environment.Should().ContainKey("CREDENTIALS_DIRECTORY").WhoseValue.Should().BeNull();
        _captured.StandardInput.Should().BeNull();
        _captured.MaxStdoutBytes.Should().Be(64 * 1024);
    }

    [Fact]
    public async Task CheckAuthAsync_ProcessRunnerThrows_Null()
    {
        // Arrange
        _processes.RunAsync(Arg.Any<ProcessSpec>(), Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("boom"));

        // Act
        bool? ok = await Runner().CheckAuthAsync(TestContext.Current.CancellationToken);

        // Assert
        ok.Should().BeNull();
    }

    [Fact]
    public async Task CheckAuthAsync_CallerCancelled_Throws()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        _processes.RunAsync(Arg.Any<ProcessSpec>(), Arg.Any<CancellationToken>()).ThrowsAsync(call => new OperationCanceledException(call.Arg<CancellationToken>()));

        // Act
        Func<Task> act = () => Runner().CheckAuthAsync(cts.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
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
    public async Task RunAsync_OutputOverCap_StillSucceedsFromItsResult()
    {
        // Arrange: spec 06 ex-Q4 — the cap bounds what is retained; the run is classified by its result
        Script(Exited(), StreamLines.Init(), StreamLines.Result());

        // Act
        var result = await Runner().RunAsync(Request() with { MaxCaptureBytes = 64 }, TestContext.Current.CancellationToken);

        // Assert
        result.Outcome.Should().Be(ModelRunOutcome.Succeeded);
        result.CostUsd.Should().NotBeNull();
    }

    [Fact]
    public async Task RunAsync_StdoutTruncatedByProcessRunner_NotAFailure()
    {
        // Arrange
        Script(Exited() with { StdoutTruncated = true }, StreamLines.Init(), StreamLines.Result());

        // Act
        var result = await Runner().RunAsync(Request(), TestContext.Current.CancellationToken);

        // Assert
        result.Outcome.Should().Be(ModelRunOutcome.Succeeded);
    }

    [Fact]
    public async Task RunAsync_Result_CarriesLastAssistantText()
    {
        // Arrange
        Script(Exited(), StreamLines.Init(), StreamLines.Assistant("thinking"), StreamLines.Assistant("REPORT\nall good"), StreamLines.Result());

        // Act
        var result = await Runner().RunAsync(Request(), TestContext.Current.CancellationToken);

        // Assert
        result.LastAssistantText.Should().Be("REPORT\nall good");
    }

    [Fact]
    public async Task RunAsync_TranscriptPath_WritesEveryLineAsReceivedUtf8NoBom()
    {
        // Arrange: spec 06 — the transcript is written as the stream arrives, into a directory the runner creates
        using var scratch = new ScratchDirectoryForTranscripts();
        var path = Path.Combine(scratch.Root, "runs", "acme", "job.jsonl");
        Script(Exited(), "line1", "line2", "line3");

        // Act
        await Runner().RunAsync(Request() with { TranscriptPath = path }, TestContext.Current.CancellationToken);

        // Assert
        File.ReadAllBytes(path).Should().Equal("line1\nline2\nline3\n"u8.ToArray());
    }

    [Fact]
    public async Task RunAsync_TranscriptPathUnwritable_WarnsOnceAndStillReturnsResult()
    {
        // Arrange: the transcript's directory would have to live under a file
        using var scratch = new ScratchDirectoryForTranscripts();
        var blocker = Path.Combine(scratch.Root, "blocker");
        File.WriteAllText(blocker, "x");
        var log = new RecordingLogger<ClaudeCodeCliRunner>();
        Script(Exited(), StreamLines.Init(), StreamLines.Assistant("secret-line-text"), StreamLines.Result());

        // Act
        var result = await Runner(log: log).RunAsync(Request() with { TranscriptPath = Path.Combine(blocker, "runs", "x.jsonl") }, TestContext.Current.CancellationToken);

        // Assert
        result.Outcome.Should().Be(ModelRunOutcome.Succeeded);
        var warning = log.Entries.Should().ContainSingle().Subject;
        warning.Level.Should().Be(Microsoft.Extensions.Logging.LogLevel.Warning);
        warning.Message.Should().Contain("transcript not written").And.NotContain("secret-line-text");
    }

    [Fact]
    public async Task RunAsync_NoTranscriptPath_WritesNothing()
    {
        // Arrange: the dream's behaviour — no transcript, no log line
        var log = new RecordingLogger<ClaudeCodeCliRunner>();
        Script(Exited(), StreamLines.Init(), StreamLines.Result());

        // Act
        var result = await Runner(log: log).RunAsync(Request(), TestContext.Current.CancellationToken);

        // Assert
        result.Outcome.Should().Be(ModelRunOutcome.Succeeded);
        log.Entries.Should().BeEmpty();
    }

    private sealed class ScratchDirectoryForTranscripts : IDisposable
    {
        public string Root { get; } = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "zyggy-unit", Guid.NewGuid().ToString("N"))).FullName;

        public void Dispose() => Directory.Delete(Root, recursive: true);
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
