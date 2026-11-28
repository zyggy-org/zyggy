using System.Text;

using NSubstitute;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Jobs;
using Zyggy.Core.Models;
using Zyggy.Core.Processes;
using Zyggy.Core.Runs;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Jobs;

/// <summary>
/// Spec 62 slice 1: a Finish step that fails is tried once more after 5 seconds; when it fails again the folder is kept and listed, the
/// body names the step and git's line, and what the model already committed is pushed to <c>zyggy/&lt;id&gt;</c> (git and model substituted).
/// </summary>
// AC-1, AC-2, AC-2b
public sealed class WorktreeFinishRetryTests : IDisposable
{
    private const string Name = "feature/checkout";

    private readonly ScratchProject _project = ScratchProject.Create();

    private readonly JobRunnerHarness _h = new();

    private readonly WorktreeGit _git;

    private readonly List<DateTimeOffset> _commitTimes = [];

    public WorktreeFinishRetryTests()
    {
        _git = new WorktreeGit(_h.Processes);
        var answer = _h.Processes.Hook!;
        _h.Processes.Hook = spec =>
        {
            if (RecordingProcessRunner.SubCommand(spec) == "commit")
            {
                _commitTimes.Add(_h.Clock.GetUtcNow());
            }

            return answer(spec);
        };
    }

    public void Dispose() => _project.Dispose();

    private string WorktreePath(Model job) => Path.Join(_h.Options.NodeDir, "worktrees", "acme", job.Header.Id.Value);

    private static string Branch(Model job) => "zyggy/" + job.Header.Id.Value;

    private Task<Model> WorktreeJobAsync(string? branch = null, string? baseBranch = null) =>
        JobRunnerHarness.JobAsync(new JobFields(_project.Root, Worktree: true, Branch: branch, Base: baseBranch));

    private static string Body(JobReply reply) => Encoding.UTF8.GetString(reply.Body.Span);

    private bool Ran(params string[] arguments) => _h.Processes.Calls.Exists(c => arguments.All(c.Arguments.Contains));

    private List<string> Pushes() => [.. _h.Processes.CallsOf("push").Select(c => c.Arguments[^1])];

    private void FailTwice(string step) => _git.FailTimes[step] = 2;

    // AC-1: every Finish step that can end in "worktree kept" is tried once more, and the run ends as if the first try had worked.
    [Theory]
    [InlineData("status")]
    [InlineData("add")]
    [InlineData("commit")]
    [InlineData("tip")]
    [InlineData("rev-list")]
    public async Task RunAsync_FinishStepFailsOnce_RetriedAndDoneAsIfNothingFailed(string step)
    {
        // Arrange
        _git.FailTimes[step] = 1;
        var job = await WorktreeJobAsync();

        // Act
        var reply = await _h.RunAdvancingAsync(job);

        // Assert
        new { reply.Status, reply.Reason, reply.DiffRef, Body = Body(reply), Removed = Ran("worktree", "remove") }.Should().BeEquivalentTo(new
        {
            Status = ReportStatus.Done,
            Reason = (string?)null,
            DiffRef = WorktreeGit.TipSha,
            Body = "done\n\nbranch " + Branch(job) + " pushed at " + WorktreeGit.TipSha + "\n",
            Removed = true,
        });
    }

    // AC-1: the second try comes after a 5-second wait on the runner's clock.
    [Fact]
    public async Task RunAsync_CommitFailsOnce_SecondTryAfterFiveSeconds()
    {
        // Arrange
        _git.FailTimes["commit"] = 1;
        var job = await WorktreeJobAsync();

        // Act
        await _h.RunAdvancingAsync(job);

        // Assert
        (_commitTimes[1] - _commitTimes[0]).Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(5));
    }

    // AC-2: the node's commit fails twice after the model committed: its commits go to zyggy/<id>, the folder and branch stay.
    [Fact]
    public async Task RunAsync_CommitFailsTwiceModelCommitted_FailedGitErrorJobBranchPushedFolderKept()
    {
        // Arrange
        FailTwice("commit");
        var job = await WorktreeJobAsync();

        // Act
        var reply = await _h.RunAdvancingAsync(job);

        // Assert
        new
        {
            reply.Status,
            reply.Reason,
            Body = Body(reply),
            reply.DiffRef,
            Pushes = Pushes(),
            Removed = Ran("worktree", "remove"),
            BranchDeleted = Ran("branch", "-D"),
        }.Should().BeEquivalentTo(new
        {
            Status = ReportStatus.Failed,
            Reason = "git_error",
            Body = "done\n\ngit_error: commit (fatal: commit failed)\nworktree kept at " + WorktreePath(job) + "\nbranch " + Branch(job) + " pushed at "
                + WorktreeGit.TipSha + " (without the uncommitted changes)\n",
            DiffRef = WorktreeGit.TipSha,
            Pushes = new[] { "refs/heads/" + Branch(job) + ":refs/heads/" + Branch(job) },
            Removed = false,
            BranchDeleted = false,
        });
    }

    // AC-2: without commits of the model nothing is pushed.
    [Fact]
    public async Task RunAsync_AddFailsTwiceNothingCommitted_KeptNoPush()
    {
        // Arrange
        FailTwice("add");
        _git.Ahead = 0;
        var job = await WorktreeJobAsync();

        // Act
        var reply = await _h.RunAdvancingAsync(job);

        // Assert
        new { reply.Status, Body = Body(reply), reply.DiffRef, Pushes = Pushes() }.Should().BeEquivalentTo(new
        {
            Status = ReportStatus.Failed,
            Body = "done\n\ngit_error: commit (fatal: add failed)\nworktree kept at " + WorktreePath(job) + "\n",
            DiffRef = (string?)null,
            Pushes = Array.Empty<string>(),
        });
    }

    // AC-2: a refused push says so with git's line; the branch stays locally.
    [Fact]
    public async Task RunAsync_CommitFailsTwicePushRefused_KeptLocallyWithGitsLine()
    {
        // Arrange
        FailTwice("commit");
        _git.Failing.Add("push");
        var job = await WorktreeJobAsync();

        // Act
        var reply = await _h.RunAdvancingAsync(job);

        // Assert
        new { Body = Body(reply), reply.DiffRef, BranchDeleted = Ran("branch", "-D") }.Should().BeEquivalentTo(new
        {
            Body = "done\n\ngit_error: commit (fatal: commit failed)\nworktree kept at " + WorktreePath(job) + "\nbranch " + Branch(job)
                + " kept locally, not pushed: push (fatal: push failed)\n",
            DiffRef = (string?)null,
            BranchDeleted = false,
        });
    }

    // AC-2: a project without an origin keeps the branch locally and says why.
    [Fact]
    public async Task RunAsync_CommitFailsTwiceNoOrigin_KeptLocallyNoOrigin()
    {
        // Arrange
        FailTwice("commit");
        _git.OriginUrl = null;
        var job = await WorktreeJobAsync();

        // Act
        var reply = await _h.RunAdvancingAsync(job);

        // Assert
        new { Body = Body(reply), Pushes = Pushes() }.Should().BeEquivalentTo(new
        {
            Body = "done\n\ngit_error: commit (fatal: commit failed)\nworktree kept at " + WorktreePath(job) + "\nbranch " + Branch(job)
                + " kept locally, not pushed: push (no origin)\n",
            Pushes = Array.Empty<string>(),
        });
    }

    // AC-2: reading the status fails twice: the model's commits are still pushed.
    [Fact]
    public async Task RunAsync_StatusFailsTwice_StatusStepNamedCommitsPushed()
    {
        // Arrange
        FailTwice("status");
        _git.StatusAfterRun = string.Empty;
        var job = await WorktreeJobAsync();

        // Act
        var reply = await _h.RunAdvancingAsync(job);

        // Assert
        new { reply.Status, Body = Body(reply), Committed = Ran("commit") }.Should().BeEquivalentTo(new
        {
            Status = ReportStatus.Failed,
            Body = "done\n\ngit_error: status (fatal: status failed)\nworktree kept at " + WorktreePath(job) + "\nbranch " + Branch(job) + " pushed at "
                + WorktreeGit.TipSha + " (without the uncommitted changes)\n",
            Committed = false,
        });
    }

    // AC-2: reading the branch tip or its count fails twice: rev-parse with git's line, nothing pushed, nothing removed.
    [Theory]
    [InlineData("tip")]
    [InlineData("rev-list")]
    public async Task RunAsync_TipReadFailsTwice_RevParseStepNamedNothingPushed(string step)
    {
        // Arrange
        FailTwice(step);
        var job = await WorktreeJobAsync();

        // Act
        var reply = await _h.RunAdvancingAsync(job);

        // Assert
        new { reply.Status, reply.Reason, Body = Body(reply), Pushes = Pushes(), Removed = Ran("worktree", "remove") }.Should().BeEquivalentTo(new
        {
            Status = ReportStatus.Failed,
            Reason = "git_error",
            Body = "done\n\ngit_error: rev-parse (fatal: " + step + " failed)\nworktree kept at " + WorktreePath(job) + "\n",
            Pushes = Array.Empty<string>(),
            Removed = false,
        });
    }

    // AC-2: the kept folder is listed for zyggy status with the step and git's line.
    [Fact]
    public async Task RunAsync_CommitFailsTwice_FolderListedInKeptWorktrees()
    {
        // Arrange
        FailTwice("commit");
        var job = await WorktreeJobAsync();

        // Act
        await _h.RunAdvancingAsync(job);

        // Assert
        LeftoverWorktrees.ReadKept(_h.Options.NodeDir!, TenantId.Parse("acme")).Should().BeEquivalentTo(new[]
        {
            new KeptWorktree(job.Header.Id.Value, WorktreePath(job), "commit: fatal: commit failed"),
        });
    }

    // AC-2: a timed-out run keeps its own status and reason; the lines are added.
    [Fact]
    public async Task RunAsync_TimeoutCommitFailsTwice_KeepsTimeoutLinesAdded()
    {
        // Arrange
        FailTwice("commit");
        _h.Model.RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>()).Returns(JobRunnerHarness.Failed(RunFailureReason.Timeout, "timeout", "partial"));
        var job = await WorktreeJobAsync();

        // Act
        var reply = await _h.RunAdvancingAsync(job);

        // Assert
        new { reply.Status, reply.Reason, Body = Body(reply) }.Should().BeEquivalentTo(new
        {
            Status = ReportStatus.Timeout,
            Reason = "timeout",
            Body = "timeout after 30 min\n\npartial\ngit_error: commit (fatal: commit failed)\nworktree kept at " + WorktreePath(job) + "\nbranch "
                + Branch(job) + " pushed at " + WorktreeGit.TipSha + " (without the uncommitted changes)\n",
        });
    }

    // AC-2: a job with base never moves it when Finish keeps the folder.
    [Fact]
    public async Task RunAsync_BaseJobCommitFailsTwice_BaseNotPushed()
    {
        // Arrange
        FailTwice("commit");
        var job = await WorktreeJobAsync(baseBranch: "feature/shop");

        // Act
        await _h.RunAdvancingAsync(job);

        // Assert
        Pushes().Should().Equal("refs/heads/" + Branch(job) + ":refs/heads/" + Branch(job));
    }

    // AC-2b: a done run on a named branch whose model committed once and whose node add fails twice: the commit goes to zyggy/<id>,
    // the named branch stays where it was, and the body says both.
    [Fact]
    public async Task RunAsync_NamedDoneModelCommittedAddFailsTwice_JobBranchPushedNamedBranchNotChanged()
    {
        // Arrange
        FailTwice("add");
        var job = await WorktreeJobAsync(branch: Name);

        // Act
        var reply = await _h.RunAdvancingAsync(job);

        // Assert
        new { reply.Status, reply.Reason, Body = Body(reply), Pushes = Pushes() }.Should().BeEquivalentTo(new
        {
            Status = ReportStatus.Failed,
            Reason = "git_error",
            Body = "done\n\ngit_error: commit (fatal: add failed)\nworktree kept at " + WorktreePath(job) + "\nbranch " + Branch(job) + " pushed at "
                + WorktreeGit.TipSha + " (without the uncommitted changes)\nbranch " + Name + " not changed: worktree kept\n",
            Pushes = new[] { "refs/heads/" + Branch(job) + ":refs/heads/" + Branch(job) },
        });
    }
}
