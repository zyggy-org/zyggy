using System.Text;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Models;
using Zyggy.Core.Runs;
using Zyggy.Core.Tests.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Jobs;

/// <summary>Spec 16 slice 2: every way git can fail in a worktree run ends in a report, with the work kept (git and model substituted).</summary>
public sealed class WorktreeFailureTests : IDisposable
{
    private static readonly string[] NewFile = ["src/new.txt"];

    // A worktree run answers done or git_error: the substituted model succeeds, so no other closed reason can appear.
    private static readonly string?[] ClosedReasons = [null, "git_error"];

    private readonly ScratchProject _project = ScratchProject.Create();

    private readonly JobRunnerHarness _h = new();

    private readonly WorktreeGit _git;

    public WorktreeFailureTests() => _git = new WorktreeGit(_h.Processes);

    public void Dispose() => _project.Dispose();

    private string WorktreePath(Model job) => Path.Join(_h.Options.NodeDir, "worktrees", "acme", job.Header.Id.Value);

    private static string Branch(Model job) => "zyggy/" + job.Header.Id.Value;

    private Task<Model> WorktreeJobAsync() => JobRunnerHarness.JobAsync(new JobFields(_project.Root, Worktree: true));

    private bool Ran(params string[] arguments) => _h.Processes.Calls.Exists(c => arguments.All(c.Arguments.Contains));

    private int ModelCalls() => _h.Model.ReceivedCalls().Count();

    private void ModelTimesOut() =>
        _h.Model.RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>()).Returns(JobRunnerHarness.Failed(RunFailureReason.Timeout, "timeout", "partial"));

    private void ModelFails() =>
        _h.Model.RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>()).Returns(JobRunnerHarness.Failed(RunFailureReason.ClaudeError, "exit 1", "partial"));

    private static string Body(JobReply reply) => Encoding.UTF8.GetString(reply.Body.Span);

    // Row 1: no default branch / worktree cannot be created (AC-9).
    [Fact]
    public async Task RunAsync_NoDefaultBranch_FailedGitErrorModelNeverStarted()
    {
        // Arrange
        _git.NoDefaultBranch = true;
        var job = await WorktreeJobAsync();

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        new { reply.Status, reply.Reason, Body = Body(reply), reply.FilesChanged, reply.DiffRef, Model = ModelCalls() }.Should().BeEquivalentTo(new
        {
            Status = ReportStatus.Failed,
            Reason = "git_error",
            Body = "git_error: default_branch\n",
            FilesChanged = Array.Empty<string>(),
            DiffRef = (string?)null,
            Model = 0,
        });
    }

    [Fact]
    public async Task RunAsync_WorktreeAddFails_FailedGitErrorModelNeverStarted()
    {
        // Arrange
        _git.Failing.Add("worktree add");
        var job = await WorktreeJobAsync();

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        new { reply.Status, reply.Reason, Body = Body(reply), reply.FilesChanged, Model = ModelCalls() }.Should().BeEquivalentTo(new
        {
            Status = ReportStatus.Failed,
            Reason = "git_error",
            Body = "git_error: worktree_add\n",
            FilesChanged = Array.Empty<string>(),
            Model = 0,
        });
    }

    // Row 2: fetch fails → one warning, the run proceeds.
    [Fact]
    public async Task RunAsync_FetchFails_DoneWithOneWarning()
    {
        // Arrange
        _git.Failing.Add("fetch");
        var job = await WorktreeJobAsync();

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        new { reply.Status, reply.DiffRef, Warnings = _h.Log.AtLevel(LogLevel.Warning).Count(e => e.Message.Contains("fetch", StringComparison.Ordinal)) }
            .Should().BeEquivalentTo(new { Status = ReportStatus.Done, DiffRef = WorktreeGit.TipSha, Warnings = 1 });
    }

    // Row 3: the node's commit fails → worktree and branch stay, no push.
    [Fact]
    public async Task RunAsync_CommitFails_FailedGitErrorWorktreeKeptNoPush()
    {
        // Arrange
        _git.Failing.Add("commit");
        var job = await WorktreeJobAsync();

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        new
        {
            reply.Status,
            reply.Reason,
            Body = Body(reply),
            reply.DiffRef,
            Pushed = Ran("push"),
            Removed = Ran("worktree", "remove"),
            BranchDeleted = Ran("branch", "-D"),
        }.Should().BeEquivalentTo(new
        {
            Status = ReportStatus.Failed,
            Reason = "git_error",
            Body = "done\n\nworktree kept at " + WorktreePath(job) + ": commit\n",
            DiffRef = (string?)null,
            Pushed = false,
            Removed = false,
            BranchDeleted = false,
        });
    }

    // Row 4: push fails after a done run with commits → local branch kept, worktree removed.
    [Fact]
    public async Task RunAsync_PushFails_FailedGitErrorBranchKeptLocally()
    {
        // Arrange
        _git.Failing.Add("push");
        var job = await WorktreeJobAsync();

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        new
        {
            reply.Status,
            reply.Reason,
            Body = Body(reply),
            reply.FilesChanged,
            reply.DiffRef,
            Removed = Ran("worktree", "remove"),
            BranchDeleted = Ran("branch", "-D"),
        }.Should().BeEquivalentTo(new
        {
            Status = ReportStatus.Failed,
            Reason = "git_error",
            Body = "done\n\nbranch " + Branch(job) + " kept locally, not pushed: push\n",
            FilesChanged = NewFile,
            DiffRef = (string?)null,
            Removed = true,
            BranchDeleted = false,
        });
    }

    [Fact]
    public async Task RunAsync_NoOrigin_FailedGitErrorBranchKeptNoPushAttempted()
    {
        // Arrange
        _git.OriginUrl = null;
        var job = await WorktreeJobAsync();

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        new { reply.Status, reply.Reason, reply.DiffRef, Fetched = Ran("fetch"), Pushed = Ran("push"), BranchDeleted = Ran("branch", "-D") }.Should().BeEquivalentTo(new
        {
            Status = ReportStatus.Failed,
            Reason = "git_error",
            DiffRef = (string?)null,
            Fetched = false,
            Pushed = false,
            BranchDeleted = false,
        });
    }

    // Row 5: push fails after a timeout or failed run → the run's own status and reason stay.
    [Theory]
    [InlineData("timeout", ReportStatus.Timeout, "timeout")]
    [InlineData("failed", ReportStatus.Failed, "claude_error")]
    public async Task RunAsync_PushFailsAfterUnsuccessfulRun_KeepsRunStatusAndReason(string run, ReportStatus status, string reason)
    {
        // Arrange
        _git.Failing.Add("push");
        Action arrange = run == "timeout" ? ModelTimesOut : ModelFails;
        arrange();
        var job = await WorktreeJobAsync();

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        new { reply.Status, reply.Reason, reply.DiffRef, KeptLine = Body(reply).EndsWith("branch " + Branch(job) + " kept locally, not pushed: push\n", StringComparison.Ordinal) }
            .Should().BeEquivalentTo(new { Status = status, Reason = reason, DiffRef = (string?)null, KeptLine = true });
    }

    // Row 6: the model times out → Finish still commits, pushes and removes (AC-7).
    [Fact]
    public async Task RunAsync_ModelTimesOut_TimeoutWithDiffRefAndWorktreeRemoved()
    {
        // Arrange
        ModelTimesOut();
        var job = await WorktreeJobAsync();

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        new { reply.Status, reply.Reason, reply.DiffRef, Committed = Ran("commit"), Pushed = Ran("push"), Removed = Ran("worktree", "remove", WorktreePath(job)) }
            .Should().BeEquivalentTo(new { Status = ReportStatus.Timeout, Reason = "timeout", DiffRef = WorktreeGit.TipSha, Committed = true, Pushed = true, Removed = true });
    }

    // Row 7: the worktree folder cannot be removed → one warning, report unaffected.
    [Fact]
    public async Task RunAsync_RemoveFails_DoneUnaffectedWithOneWarning()
    {
        // Arrange
        _git.Failing.Add("worktree remove");
        var job = await WorktreeJobAsync();

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        new
        {
            reply.Status,
            reply.DiffRef,
            Warnings = _h.Log.AtLevel(LogLevel.Warning).Count(e => e.Message.Contains("not removed", StringComparison.Ordinal)),
            BranchDeleted = Ran("branch", "-D"),
        }.Should().BeEquivalentTo(new { Status = ReportStatus.Done, DiffRef = WorktreeGit.TipSha, Warnings = 1, BranchDeleted = false });
    }

    // Row 8: a git command that hangs is abandoned by the process runner after 2 minutes and counts as that step failing (AC-14).
    [Theory]
    [InlineData("worktree add", "git_error: worktree_add\n")]
    [InlineData("commit", "kept at")]
    [InlineData("push", "not pushed: push")]
    public async Task RunAsync_GitStepTimesOut_TreatedAsThatStepFailing(string step, string bodyPart)
    {
        // Arrange
        _git.TimingOut.Add(step);
        var job = await WorktreeJobAsync();

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        new { reply.Status, reply.Reason, Part = Body(reply).Contains(bodyPart, StringComparison.Ordinal) }
            .Should().BeEquivalentTo(new { Status = ReportStatus.Failed, Reason = "git_error", Part = true });
    }

    [Fact]
    public async Task RunAsync_WorktreeJob_EveryGitCommandBoundedAndNonInteractive()
    {
        // Arrange
        var job = await WorktreeJobAsync();

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        _h.Processes.Calls.Should().HaveCountGreaterThan(10).And.OnlyContain(c =>
            c.Timeout == TimeSpan.FromMinutes(2) && c.Environment.ContainsKey("GIT_TERMINAL_PROMPT") && c.Environment["GIT_TERMINAL_PROMPT"] == "0");
    }

    // AC-13: whatever fails, the runner answers; no reason outside the closed enum.
    [Theory]
    [InlineData("fetch")]
    [InlineData("worktree add")]
    [InlineData("commit")]
    [InlineData("push")]
    [InlineData("worktree remove")]
    [InlineData("status")]
    [InlineData("rev-list")]
    [InlineData("diff")]
    public async Task RunAsync_AnyGitStepFails_RepliesWithClosedReason(string step)
    {
        // Arrange
        _git.Failing.Add(step);
        var job = await WorktreeJobAsync();

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        ClosedReasons.Should().Contain(reply.Reason);
    }
}
