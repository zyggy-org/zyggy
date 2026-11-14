using System.Text;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Models;
using Zyggy.Core.Processes;
using Zyggy.Core.Runs;
using Zyggy.Core.Tests.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Jobs;

/// <summary>
/// Spec 15 slice 2 (AC-6, AC-7, AC-9 at unit level): the runner arms the <c>PreToolUse</c> hook on every job run, does not start the
/// model when the hook binary is missing, and adds one <c>blocked</c> result line when the hook refused calls.
/// </summary>
public sealed class JobRunnerHookTests : IDisposable
{
    private readonly ScratchProject _project = ScratchProject.Create();

    private readonly JobRunnerHarness _h = new();

    public void Dispose() => _project.Dispose();

    private string BlockedPath(Model job) => Path.Join(_h.Options.NodeDir, "runs", "acme", job.Header.Id.Value + ".blocked");

    private string WorktreePath(Model job) => Path.Join(_h.Options.NodeDir, "worktrees", "acme", job.Header.Id.Value);

    private static string BodyOf(JobReply reply) => Encoding.UTF8.GetString(reply.Body.Span);

    // The hook writes these lines while the model runs; the substitute does the same.
    private void ModelRefuses(Model job, ModelRunResult result, params string[] lines) =>
        _h.Model.RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            File.AppendAllText(BlockedPath(job), string.Concat(lines.Select(l => l + "\n")));
            return result;
        });

    [Fact]
    public async Task RunAsync_HookBinaryMissing_FailedClaudeErrorHookUnavailableModelNeverStarted()
    {
        // Arrange
        var missing = Path.Join(Path.GetTempPath(), "zyggy-unit-missing", "zyggy");
        _h.Options.HookExecutable = missing;
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root));

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        new { reply.Status, reply.Reason, Body = BodyOf(reply), ModelCalls = _h.Model.ReceivedCalls().Count() }
            .Should().BeEquivalentTo(new { Status = ReportStatus.Failed, Reason = "claude_error", Body = $"claude_error: hook unavailable: {missing}\n", ModelCalls = 0 });
    }

    [Fact]
    public async Task RunAsync_HookBinaryMissing_OneWarningNamingThePath()
    {
        // Arrange
        var missing = Path.Join(Path.GetTempPath(), "zyggy-unit-missing", "zyggy");
        _h.Options.HookExecutable = missing;
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root));

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        _h.Log.AtLevel(LogLevel.Warning).Should().ContainSingle()
            .Which.Message.Should().Be($"job {job.Header.Id.Value}: hook unavailable: {missing}");
    }

    [Fact]
    public async Task RunAsync_HookBinaryMissingWorktreeJob_NoGitCallNoLock()
    {
        // Arrange
        _h.Options.HookExecutable = Path.Join(Path.GetTempPath(), "zyggy-unit-missing", "zyggy");
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root, Worktree: true));

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert: pre-flight's rev-parse only; no worktree, no lock file
        new { Worktree = _h.Processes.CallsOf("worktree").Count(), Lock = File.Exists(_project.LockPath) }
            .Should().BeEquivalentTo(new { Worktree = 0, Lock = false });
    }

    // Review of 2026-11-14 (finding 4): a binary that exists but does not refuse an empty call would let every tool call through.
    public static TheoryData<string, ProcessResult> SelfTestFailures() => new()
    {
        { "allows", RecordingProcessRunner.Ok() },
        { "exit 1", RecordingProcessRunner.Fail(1, "boom") },
        { "timed out", new ProcessResult(null, string.Empty, string.Empty, TimedOut: true, StartFailed: false, StdoutTruncated: false, TimeSpan.FromSeconds(10)) },
        { "did not start", new ProcessResult(null, string.Empty, string.Empty, TimedOut: false, StartFailed: true, StdoutTruncated: false, TimeSpan.Zero) },
    };

    [Theory]
    [MemberData(nameof(SelfTestFailures))]
    public async Task RunAsync_HookSelfTestNotRefused_FailedHookUnavailableModelNeverStartedNoLock(string why, ProcessResult selfTest)
    {
        // Arrange
        _h.Processes.On("hook", selfTest);
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root, Worktree: true));

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        new
        {
            reply.Status,
            reply.Reason,
            Body = BodyOf(reply),
            ModelCalls = _h.Model.ReceivedCalls().Count(),
            Worktree = _h.Processes.CallsOf("worktree").Count(),
            Lock = File.Exists(_project.LockPath),
        }.Should().BeEquivalentTo(
            new { Status = ReportStatus.Failed, Reason = "claude_error", Body = $"claude_error: hook unavailable: {_h.Options.HookExecutable}\n", ModelCalls = 0, Worktree = 0, Lock = false },
            why);
    }

    [Fact]
    public async Task RunAsync_HookSelfTestThrows_FailedHookUnavailableNotARunnerException()
    {
        // Arrange
        _h.Processes.Hook = spec => RecordingProcessRunner.SubCommand(spec) == "hook" ? throw new InvalidOperationException("cannot start") : (ProcessResult?)null;
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root));

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        BodyOf(reply).Should().Be($"claude_error: hook unavailable: {_h.Options.HookExecutable}\n");
    }

    [Fact]
    public async Task RunAsync_HookSelfTest_RunsTheBinaryOnceWithNoRulesAndNoInput()
    {
        // Arrange
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root));

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert: refused (the substitute's default), so the model ran
        var call = _h.Processes.CallsOf("hook").Should().ContainSingle().Subject;
        new { call.FileName, Arguments = string.Join(' ', call.Arguments), call.StandardInput, Bounded = call.Timeout > TimeSpan.Zero && call.Timeout <= TimeSpan.FromSeconds(30), ModelCalls = _h.Model.ReceivedCalls().Count() }
            .Should().BeEquivalentTo(new { FileName = _h.Options.HookExecutable, Arguments = "hook pre-tool-use", StandardInput = (string?)null, Bounded = true, ModelCalls = 1 });
    }

    [Fact]
    public async Task RunAsync_HookSelfTestNotRefused_OneWarningNamingThePath()
    {
        // Arrange
        _h.Processes.On("hook", RecordingProcessRunner.Ok());
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root));

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        _h.Log.AtLevel(LogLevel.Warning).Should().ContainSingle()
            .Which.Message.Should().Be($"job {job.Header.Id.Value}: hook unavailable: {_h.Options.HookExecutable}");
    }

    [Fact]
    public async Task RunAsync_InPlaceJob_HookIsTheBinaryWithRunDirectoryRulesAndBlockedFile()
    {
        // Arrange
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root, AllowedTools: ["Read", "Bash(dotnet *)"]));

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        _h.Request().PreToolUseHook.Should().BeEquivalentTo(new ModelHookCommand(
            _h.Options.HookExecutable!,
            ["hook", "pre-tool-use", "--root", _project.Root, "--blocked-file", BlockedPath(job), "--allow", "Read", "--allow", "Bash(dotnet *)"]));
    }

    [Fact]
    public async Task RunAsync_WorktreeJob_HookRootIsTheWorktree()
    {
        // Arrange
        _ = new WorktreeGit(_h.Processes);
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root, AllowedTools: ["Read"], Worktree: true));

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        _h.Request().PreToolUseHook!.Args.Should().Equal(
            "hook", "pre-tool-use", "--root", WorktreePath(job), "--blocked-file", BlockedPath(job), "--allow", "Read");
    }

    [Fact]
    public async Task RunAsync_NoAllowedToolsInJob_HookCarriesTheNodeDefault()
    {
        // Arrange
        _h.NodePolicy = _h.NodePolicy with { DefaultAllowedTools = ["Read", "Grep", "Glob", "TodoWrite"] };
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root));

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        _h.Request().PreToolUseHook!.Args.Where((_, i) => i > 0 && _h.Request().PreToolUseHook!.Args[i - 1] == "--allow")
            .Should().Equal("Read", "Grep", "Glob", "TodoWrite");
    }

    [Fact]
    public async Task RunAsync_HookExecutableUnset_DefaultsToZyggyBesideTheRunningProcess()
    {
        // Arrange: beside the test host there is no zyggy, so the default fails the pre-flight with that path
        _h.Options.HookExecutable = null;
        var expected = Path.Join(Path.GetDirectoryName(Environment.ProcessPath), OperatingSystem.IsWindows() ? "zyggy.exe" : "zyggy");
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root));

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        BodyOf(reply).Should().Be($"claude_error: hook unavailable: {expected}\n");
    }

    [Fact]
    public async Task RunAsync_HookRefusedTwo_DoneWithOneBlockedLineNamingTheFirst()
    {
        // Arrange
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root));
        ModelRefuses(job, JobRunnerHarness.Succeeded("done"), "Bash: network program (curl)", "Write: write outside the project (../x (y): z)");

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        new { reply.Status, Body = BodyOf(reply) }.Should().BeEquivalentTo(new
        {
            Status = ReportStatus.Done,
            Body = "done\n\nblocked: 2 tool call(s) outside this job's rules (first: Bash: network program)\n",
        });
    }

    [Fact]
    public async Task RunAsync_HookRefusedAndRunFailed_StaysFailedWithBlockedLine()
    {
        // Arrange
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root));
        ModelRefuses(job, JobRunnerHarness.Failed(RunFailureReason.ClaudeError, "exit_1"), "mcp__x__y: not in allowed_tools (no rule matches)");

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        new { reply.Status, reply.Reason, Body = BodyOf(reply) }.Should().BeEquivalentTo(new
        {
            Status = ReportStatus.Failed,
            Reason = "claude_error",
            Body = "claude_error: exit_1\n\n(no output)\n\nblocked: 1 tool call(s) outside this job's rules (first: mcp__x__y: not in allowed_tools)\n",
        });
    }

    [Fact]
    public async Task RunAsync_WorktreeJobWithRefusal_BlockedLineAfterTheWorktreeLine()
    {
        // Arrange
        _ = new WorktreeGit(_h.Processes);
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root, Worktree: true));
        ModelRefuses(job, JobRunnerHarness.Succeeded("done"), "Edit: hook error (internal error)");

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        BodyOf(reply).Should().Be(
            $"done\nbranch zyggy/{job.Header.Id.Value} pushed at {WorktreeGit.TipSha}\nblocked: 1 tool call(s) outside this job's rules (first: Edit: hook error)\n");
    }

    [Fact]
    public async Task RunAsync_NoRefusals_NoBlockedLine()
    {
        // Arrange
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root));
        _h.Model.RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>()).Returns(JobRunnerHarness.Succeeded("done"));

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        BodyOf(reply).Should().Be("done\n");
    }

    [Fact]
    public async Task RunAsync_NoRulesInJobOrNodeDefault_HookCarriesReadGrepGlob()
    {
        // Arrange: spec 15 "the envelope's, else claude.defaultAllowedTools, else Read, Grep, Glob" — never an empty, deny-all list by accident
        _h.NodePolicy = _h.NodePolicy with { DefaultAllowedTools = [] };
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root));

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        _h.Request().PreToolUseHook!.Args.Skip(6).Should().Equal("--allow", "Read", "--allow", "Grep", "--allow", "Glob");
    }

    [Fact]
    public async Task RunAsync_StaleBlockedFileOfAnEarlierRun_ClearedBeforeTheModelStarts()
    {
        // Arrange
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root));
        Directory.CreateDirectory(Path.GetDirectoryName(BlockedPath(job))!);
        File.WriteAllText(BlockedPath(job), "Bash: network program (curl)\n");
        _h.Model.RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>()).Returns(JobRunnerHarness.Succeeded("done"));

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        BodyOf(reply).Should().Be("done\n");
    }

    [Fact]
    public async Task RunAsync_OldBlockedFile_SweptWithTheTranscripts()
    {
        // Arrange
        var directory = Path.Join(_h.Options.NodeDir, "runs", "acme");
        Directory.CreateDirectory(directory);
        var old = Path.Join(directory, "a.blocked");
        File.WriteAllText(old, "Bash: network program (curl)\n");
        File.SetLastWriteTimeUtc(old, (JobRunnerHarness.T - TimeSpan.FromDays(31)).UtcDateTime);
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root));

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        File.Exists(old).Should().BeFalse();
    }
}
