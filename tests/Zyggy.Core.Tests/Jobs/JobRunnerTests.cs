using System.Text;

using Microsoft.Extensions.Logging;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using Zyggy.Core.Envelope;
using Zyggy.Core.Jobs;
using Zyggy.Core.Models;
using Zyggy.Core.Runs;
using Zyggy.Core.Tests.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Jobs;

/// <summary>Plan 06 Steps 1–2: the §6 pipeline of one claimed job over substitutes.</summary>
public sealed class JobRunnerTests : IDisposable
{
    private readonly ScratchProject _project = ScratchProject.Create();

    private readonly JobRunnerHarness _h = new();

    public void Dispose() => _project.Dispose();

    [Fact]
    public async Task RunAsync_ProjectPathWithGitAndClaude_RunsModelOnceInThatDirectoryAndRepliesDone()
    {
        // Arrange
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root));

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        await _h.Model.Received(1).RunAsync(Arg.Is<ModelRunRequest>(r => r.WorkingDirectory == _project.Root), Arg.Any<CancellationToken>());
        reply.Status.Should().Be(ReportStatus.Done);
        reply.Reason.Should().BeNull();
    }

    public static TheoryData<string, string> BadProjects() => new()
    {
        { "relative", "not absolute" },
        { "missing", "missing" },
        { "no-git", "not a git repository" },
        { "no-claude", "no .claude" },
        { "registry-name", "not absolute" },
    };

    [Theory]
    [MemberData(nameof(BadProjects))]
    public async Task RunAsync_BadProject_RejectedUnknownProjectWithoutModelCall(string kind, string check)
    {
        // Arrange
        using var noGit = ScratchProject.WithoutGit();
        using var noClaude = ScratchProject.WithoutClaude();
        var project = kind switch
        {
            "relative" => "src/x",
            "missing" => Path.Combine(_project.Root, "does-not-exist"),
            "no-git" => noGit.Root,
            "no-claude" => noClaude.Root,
            _ => "calizr",
        };
        if (kind == "no-git")
        {
            _h.Processes.On("rev-parse", RecordingProcessRunner.Fail(128, "fatal: not a git repository"));
        }

        var job = await JobRunnerHarness.JobAsync(new JobFields(project));

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        reply.Status.Should().Be(ReportStatus.Rejected);
        reply.Reason.Should().Be("unknown_project");
        Encoding.UTF8.GetString(reply.Body.Span).Should().Be($"unknown_project: {check}\n");
        await _h.Model.DidNotReceiveWithAnyArgs().RunAsync(default!, TestContext.Current.CancellationToken);
        var warning = _h.Log.AtLevel(LogLevel.Warning).Should().ContainSingle().Subject;
        warning.Message.Should().Contain(job.Header.Id.Value).And.Contain(check);
        _h.Log.Messages.Should().NotContain(m => m.Contains(ScratchProject.MarkerFileName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_AgentPresent_PromptNamesTheSubagent()
    {
        // Arrange
        _project.WithAgent("env-debugger");
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root, Agent: "env-debugger"));

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        reply.Status.Should().Be(ReportStatus.Done);
        _h.Request().Prompt.Should().Contain("Use the env-debugger subagent for this task.");
    }

    [Theory]
    [InlineData("db-migrator")]
    [InlineData("../../etc/passwd")]
    [InlineData("env debugger")]
    [InlineData("agents/x")]
    public async Task RunAsync_AgentAbsentOrUnsafe_RejectedUnknownAgent(string agent)
    {
        // Arrange
        _project.WithAgent("env-debugger");
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root, Agent: agent));

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        reply.Status.Should().Be(ReportStatus.Rejected);
        reply.Reason.Should().Be("unknown_agent");
        await _h.Model.DidNotReceiveWithAnyArgs().RunAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RunAsync_NotAJob_ThrowsArgumentException()
    {
        // Arrange
        var header = new EnvelopeHeader(TestKeys.Acme1.Tenant, EnvelopeId.New(JobRunnerHarness.T), JobRunnerHarness.HomeLaptop, JobRunnerHarness.Central, JobRunnerHarness.T, InReplyTo: EnvelopeId.New(JobRunnerHarness.T));
        var report = Model.CreateReport(header, new ReportFields(ReportStatus.Done), "ok\n"u8.ToArray());

        // Act
        var act = () => _h.Build().RunAsync(report, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task RunAsync_WorktreeTrue_RunsInPlaceWithOneWarning()
    {
        // Arrange
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root, Worktree: true));

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        reply.Status.Should().Be(ReportStatus.Done);
        _h.Request().WorkingDirectory.Should().Be(_project.Root);
        _h.Log.AtLevel(LogLevel.Warning).Should().ContainSingle().Which.Message.Should().Contain("worktree requested");
    }

    [Theory]
    [InlineData(10, null, 600)]
    [InlineData(null, null, 1800)]
    [InlineData(10, 2, 2)]
    public async Task RunAsync_TimeoutFromJobElsePolicy_SetsRequestTimeout(int? jobMinutes, int? overrideSeconds, int expectedSeconds)
    {
        // Arrange
        _h.Options.TimeoutOverride = overrideSeconds is { } s ? TimeSpan.FromSeconds(s) : null;
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root, TimeoutMinutes: jobMinutes));

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        _h.Request().Timeout.Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Fact]
    public async Task RunAsync_StartedFinished_FromFakeClock()
    {
        // Arrange
        _h.Model.RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _h.Clock.Advance(TimeSpan.FromSeconds(65));
            return JobRunnerHarness.Succeeded("done\n");
        });
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root));

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        reply.Started.Should().Be(JobRunnerHarness.T);
        (reply.Finished - reply.Started).Should().Be(TimeSpan.FromSeconds(65));
    }

    [Fact]
    public async Task RunAsync_LogsOncePerStateChange()
    {
        // Arrange
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root));

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        _h.Log.Messages.Where(m => m.Contains(": started in ", StringComparison.Ordinal)).Should().ContainSingle();
        _h.Log.Messages.Where(m => m.Contains(": finished ", StringComparison.Ordinal)).Should().ContainSingle();
        _h.Log.Entries.Should().HaveCount(2);
        foreach (var entry in _h.Log.Entries)
        {
            var scope = entry.Scopes.OfType<IReadOnlyDictionary<string, object>>().Should().ContainSingle().Subject;
            scope.Should().Contain("tenant", "acme").And.Contain("machine", "home-laptop").And.Contain("phase", "run").And.Contain("jobId", job.Header.Id.Value);
        }
    }

    [Fact]
    public async Task RunAsync_LockHeldByAnother_FailedLockedWithoutModelCall()
    {
        // Arrange
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root));
        using var other = ProjectLock.TryAcquire(_project.Root, EnvelopeId.New(JobRunnerHarness.T), 1, JobRunnerHarness.T);

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        reply.Status.Should().Be(ReportStatus.Failed);
        reply.Reason.Should().Be("locked");
        Encoding.UTF8.GetString(reply.Body.Span).Should().Be($"locked: {_project.LockPath}\n");
        await _h.Model.DidNotReceiveWithAnyArgs().RunAsync(default!, TestContext.Current.CancellationToken);
        _h.Log.AtLevel(LogLevel.Warning).Should().ContainSingle();
    }

    [Fact]
    public async Task RunAsync_Done_LockReleasedAfterwards()
    {
        // Arrange
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root));

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        reply.Status.Should().Be(ReportStatus.Done);
        ProjectLock.IsHeld(_project.Root).Should().BeFalse();
        File.Exists(_project.LockPath).Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_LockIsHeldDuringTheModelCall()
    {
        // Arrange
        bool? heldDuring = null;
        _h.Model.RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            heldDuring = ProjectLock.IsHeld(_project.Root);
            return JobRunnerHarness.Succeeded("done\n");
        });
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root));

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        heldDuring.Should().BeTrue();
    }

    public static TheoryData<Exception> RunnerExceptions() => new()
    {
        new InvalidOperationException("secret body text leaks here"),
        new IOException("secret body text leaks here"),
    };

    [Theory]
    [MemberData(nameof(RunnerExceptions))]
    public async Task RunAsync_ModelRunnerThrows_FailedClaudeErrorRunnerExceptionNeverThrows(Exception thrown)
    {
        // Arrange
        _h.Model.RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(thrown);
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root));

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        var type = thrown.GetType().Name;
        reply.Status.Should().Be(ReportStatus.Failed);
        reply.Reason.Should().Be("claude_error");
        Encoding.UTF8.GetString(reply.Body.Span).Should().Be($"claude_error: runner_exception {type}\n");
        var error = _h.Log.AtLevel(LogLevel.Error).Should().ContainSingle().Subject;
        error.Message.Should().Contain(type).And.NotContain(thrown.Message);
        error.Exception.Should().BeNull();
        ProjectLock.IsHeld(_project.Root).Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_CallerCancelled_PropagatesOperationCanceledAndReleasesLock()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        _h.Model.RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>()).Returns<ModelRunResult>(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root));

        // Act
        var act = () => _h.Build().RunAsync(job, cts.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        ProjectLock.IsHeld(_project.Root).Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_PreFlightRejected_NoLockFileCreated()
    {
        // Arrange
        using var noClaude = ScratchProject.WithoutClaude();
        var job = await JobRunnerHarness.JobAsync(new JobFields(noClaude.Root));

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        reply.Status.Should().Be(ReportStatus.Rejected);
        Directory.Exists(Path.Combine(noClaude.Root, ".claude")).Should().BeFalse();
        File.Exists(_project.LockPath).Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_Succeeded_ReplyCarriesCostTurnsTokensModelAndEmptyFilesChanged()
    {
        // Arrange
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root));

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        reply.CostUsd.Should().Be(0.42m);
        reply.NumTurns.Should().Be(12);
        reply.InputTokens.Should().Be(1200);
        reply.OutputTokens.Should().Be(300);
        reply.Model.Should().Be("claude-sonnet-4-5");
        reply.FilesChanged.Should().NotBeNull().And.BeEmpty();
        reply.DiffRef.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_Succeeded_BodyIsExtractedSummaryPlusNewline()
    {
        // Arrange
        _h.Model.RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(JobRunnerHarness.Succeeded("I read the logs.\n\n## REPORT\nsummary: fixed\nfiles_changed: []"));
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root));

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        Encoding.UTF8.GetString(reply.Body.Span).Should().Be("## REPORT\nsummary: fixed\nfiles_changed: []\n");
    }

    [Fact]
    public async Task RunAsync_TokensOverIntMax_Clamped()
    {
        // Arrange
        _h.Model.RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(JobRunnerHarness.Succeeded("ok") with { InputTokens = 5_000_000_000L, OutputTokens = 3L });
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root));

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        reply.InputTokens.Should().Be(int.MaxValue);
        reply.OutputTokens.Should().Be(3);
    }

    [Fact]
    public async Task RunAsync_Timeout_StatusTimeoutReasonTimeoutBodyKeepsPartialText()
    {
        // Arrange
        _h.Model.RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(JobRunnerHarness.Failed(RunFailureReason.Timeout, "timeout", "Looking at the logs…"));
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root, TimeoutMinutes: 10));

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        reply.Status.Should().Be(ReportStatus.Timeout);
        reply.Reason.Should().Be("timeout");
        Encoding.UTF8.GetString(reply.Body.Span).Should().Be("timeout after 10 min\n\nLooking at the logs…\n");
        reply.CostUsd.Should().BeNull();
        reply.NumTurns.Should().BeNull();
    }

    [Theory]
    [InlineData("error_during_execution")]
    [InlineData("not_found")]
    [InlineData("exit_1")]
    [InlineData("no_result")]
    [InlineData("output_too_large")]
    public async Task RunAsync_ClaudeErrorDetail_FailedClaudeErrorBodyFirstLine(string detail)
    {
        // Arrange
        _h.Model.RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(JobRunnerHarness.Failed(RunFailureReason.ClaudeError, detail));
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root));

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        reply.Status.Should().Be(ReportStatus.Failed);
        reply.Reason.Should().Be("claude_error");
        Encoding.UTF8.GetString(reply.Body.Span).Should().Be($"claude_error: {detail}\n\n(no output)\n");
    }

    [Fact]
    public async Task RunAsync_OtherFailureReason_MappedToClaudeErrorWithWarning()
    {
        // Arrange
        _h.Model.RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(JobRunnerHarness.Failed(RunFailureReason.GitError, "exit_128"));
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root));

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        reply.Reason.Should().Be("claude_error");
        _h.Log.AtLevel(LogLevel.Warning).Should().ContainSingle().Which.Message.Should().Contain("git_error");
    }

    public static TheoryData<string> EveryPath() => ["done", "timeout", "claude_error", "git_error", "rejected", "locked", "exception"];

    [Theory]
    [MemberData(nameof(EveryPath))]
    public async Task RunAsync_EveryReply_ReasonIsNullOrOneOfNineWireStrings(string path)
    {
        // Arrange
        var fields = new JobFields(path == "rejected" ? "calizr" : _project.Root);
        using var held = path == "locked" ? ProjectLock.TryAcquire(_project.Root, EnvelopeId.New(JobRunnerHarness.T), 1, JobRunnerHarness.T) : null;
        var calls = _h.Model.RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>());
        switch (path)
        {
            case "timeout":
                calls.Returns(JobRunnerHarness.Failed(RunFailureReason.Timeout, "timeout"));
                break;
            case "claude_error":
                calls.Returns(JobRunnerHarness.Failed(RunFailureReason.ClaudeError, "no_result"));
                break;
            case "git_error":
                calls.Returns(JobRunnerHarness.Failed(RunFailureReason.GitError, "exit_128"));
                break;
            case "exception":
                calls.ThrowsAsync(new InvalidOperationException());
                break;
        }

        var job = await JobRunnerHarness.JobAsync(fields);

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        if (reply.Reason is { } reason)
        {
            RunFailureReasonWire.TryFromWire(reason, out _).Should().BeTrue();
            reason.Should().NotBe("dlp_filter").And.NotBe("budget_exceeded").And.NotBe("schema_unsupported").And.NotBe("git_error");
        }
        else
        {
            reply.Status.Should().Be(ReportStatus.Done);
        }
    }

    [Fact]
    public async Task RunAsync_TranscriptPath_IsNodeDirRunsTenantJobId()
    {
        // Arrange
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root));

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        _h.Request().TranscriptPath.Should().Be(Path.Join(_h.Options.NodeDir, "runs", "acme", job.Header.Id.Value + ".jsonl"));
    }

    [Fact]
    public async Task RunAsync_NodeDirNull_UsesPerOsDefault()
    {
        // Arrange
        _h.Options.NodeDir = null;
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root));

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        _h.Request().TranscriptPath.Should().Be(TranscriptPaths.For(TranscriptPaths.DefaultNodeDir(), TestKeys.Acme1.Tenant, job.Header.Id));
    }

    [Fact]
    public async Task RunAsync_OldTranscriptsOfOwnTenant_DeletedYesterdayKeptOtherTenantUntouched()
    {
        // Arrange
        var old = Transcript("acme", "a.jsonl", TimeSpan.FromDays(31));
        var recent = Transcript("acme", "b.jsonl", TimeSpan.FromDays(1));
        var foreign = Transcript("globex", "c.jsonl", TimeSpan.FromDays(40));
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root));

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        File.Exists(old).Should().BeFalse();
        File.Exists(recent).Should().BeTrue();
        File.Exists(foreign).Should().BeTrue();
        _h.Log.Messages.Should().ContainSingle(m => m.StartsWith("transcripts: removed", StringComparison.Ordinal))
            .Which.Should().Be("transcripts: removed 1 older than 30 d");
    }

    [Fact]
    public async Task RunAsync_NoOldTranscripts_NoSweepLogLine()
    {
        // Arrange
        Transcript("acme", "b.jsonl", TimeSpan.FromDays(1));
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root));

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        _h.Log.Messages.Should().NotContain(m => m.StartsWith("transcripts:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_ClockThrowsDuringSweep_FailedClaudeErrorRunnerException()
    {
        // Arrange: started (1st), lock timestamp (2nd), sweep (3rd) — the sweep's read throws
        _h.ClockOverride = new ThrowingClock(_h.Clock, throwOnCall: 3);
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root));

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        reply.Status.Should().Be(ReportStatus.Failed);
        Encoding.UTF8.GetString(reply.Body.Span).Should().Be("claude_error: runner_exception InvalidOperationException\n");
        ProjectLock.IsHeld(_project.Root).Should().BeFalse();
        await _h.Model.DidNotReceiveWithAnyArgs().RunAsync(default!, TestContext.Current.CancellationToken);
    }

    private string Transcript(string tenant, string name, TimeSpan age)
    {
        var directory = Path.Join(_h.Options.NodeDir, "runs", tenant);
        Directory.CreateDirectory(directory);
        var path = Path.Join(directory, name);
        File.WriteAllText(path, "{}\n");
        File.SetLastWriteTimeUtc(path, (JobRunnerHarness.T - age).UtcDateTime);
        return path;
    }

    private sealed class ThrowingClock(TimeProvider inner, int throwOnCall) : TimeProvider
    {
        private int _calls;

        public override DateTimeOffset GetUtcNow() =>
            ++_calls == throwOnCall ? throw new InvalidOperationException("clock fault") : inner.GetUtcNow();
    }
}
