using System.Text;

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
/// Spec 53 slice 3b: everything other than an accepted push of a <c>done</c> run falls back to <c>zyggy/&lt;id&gt;</c> under spec 16's
/// Finish, the named branch is not changed, and the report says why (git and model substituted).
/// </summary>
// AC-7b, AC-7d, AC-10, AC-11 (other rows), AC-12 (unit, re-run); AC-14 (unit) for the git-failure rows.
public sealed class WorktreeNamedBranchFallbackTests : IDisposable
{
    private const string Name = WorktreeNamedBranchTests.Name;

    private const string OriginNamedSha = WorktreeNamedBranchTests.OriginNamedSha;

    private const string NamedDestination = "refs/heads/" + Name;

    // AC-10: no git command of a named run carries a force flag or moves a branch by any other means.
    private static readonly string[] ForceArguments = ["--force", "-f", "-B", "--force-with-lease"];

    private static readonly string[] MovingSubCommands = ["reset", "update-ref", "merge", "rebase", "switch", "checkout"];

    private readonly ScratchProject _project = ScratchProject.Create();

    private readonly JobRunnerHarness _h = new();

    private readonly WorktreeGit _git;

    public WorktreeNamedBranchFallbackTests() => _git = new WorktreeGit(_h.Processes);

    public void Dispose() => _project.Dispose();

    /// <summary>The run outcome × whether the named branch is on <c>origin</c> (AC-7d).</summary>
    public static TheoryData<string, bool> NotDoneCases() => new()
    {
        { "failed", true },
        { "failed", false },
        { "timeout", true },
        { "timeout", false },
    };

    /// <summary>Every row of the Finish table plus the failed fetch (AC-10, AC-11).</summary>
    public static TheoryData<string> FinishRows() =>
    [
        "done-accepted", "done-not-ahead", "done-refused", "done-no-origin", "failed", "timeout", "commit-fails", "fetch-fails-refused", "fetch-fails-accepted",
    ];

    private string WorktreePath(Model job) => Path.Join(_h.Options.NodeDir, "worktrees", "acme", job.Header.Id.Value);

    private static string Branch(Model job) => "zyggy/" + job.Header.Id.Value;

    private Task<Model> NamedJobAsync() => JobRunnerHarness.JobAsync(new JobFields(_project.Root, Worktree: true, Branch: Name));

    private void OnOrigin() => _git.Refs["refs/remotes/origin/" + Name] = OriginNamedSha;

    private List<ProcessSpec> Git(string subCommand) => [.. _h.Processes.CallsOf(subCommand)];

    private static string Body(JobReply reply) => Encoding.UTF8.GetString(reply.Body.Span);

    private void ModelEnds(string outcome) =>
        _h.Model.RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>()).Returns(outcome == "timeout"
            ? JobRunnerHarness.Failed(RunFailureReason.Timeout, "timeout", "partial")
            : JobRunnerHarness.Failed(RunFailureReason.ClaudeError, "exit 1", "partial"));

    private static string RunBody(string outcome) => outcome == "timeout" ? "timeout after 30 min\n\npartial\n" : "claude_error: exit 1\n\npartial\n";

    private bool PushedToNamed() => Git("push").Exists(c => c.Arguments[^1].EndsWith(":" + NamedDestination, StringComparison.Ordinal));

    private void Arrange(string row)
    {
        OnOrigin();
        switch (row)
        {
            case "done-not-ahead":
                _git.StatusAfterRun = string.Empty;
                _git.Ahead = 0;
                break;
            case "done-refused":
                _git.RefusedPushDestinations.Add(NamedDestination);
                break;
            case "done-no-origin":
                _git.OriginUrl = null;
                _git.Refs.Clear();
                break;
            case "failed" or "timeout":
                ModelEnds(row);
                break;
            case "commit-fails":
                _git.Failing.Add("commit");
                break;
            case "fetch-fails-refused":
                _git.Failing.Add("fetch");
                _git.Refs.Clear();
                _git.RefusedPushDestinations.Add(NamedDestination);
                break;
            case "fetch-fails-accepted":
                _git.Failing.Add("fetch");
                _git.Refs.Clear();
                break;
        }
    }

    // AC-7d: a failed or timed-out run keeps its status; zyggy/<id> is pushed under its own name; the body says the named branch is unchanged.
    [Theory]
    [MemberData(nameof(NotDoneCases))]
    public async Task RunAsync_NamedRunNotDone_KeepsStatusJobBranchPushedNamedBranchNotChanged(string outcome, bool onOrigin)
    {
        // Arrange
        if (onOrigin)
        {
            OnOrigin();
        }

        ModelEnds(outcome);
        var job = await NamedJobAsync();

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        new { reply.Status, reply.Reason, reply.DiffRef, Body = Body(reply), Pushes = Git("push").Select(c => c.Arguments[^1]).ToList() }.Should().BeEquivalentTo(new
        {
            Status = outcome == "timeout" ? ReportStatus.Timeout : ReportStatus.Failed,
            Reason = outcome == "timeout" ? "timeout" : "claude_error",
            DiffRef = WorktreeGit.TipSha,
            Body = RunBody(outcome) + "branch " + Branch(job) + " pushed at " + WorktreeGit.TipSha + "\nbranch " + Name + " not changed: run ended " + outcome + "\n",
            Pushes = new[] { "refs/heads/" + Branch(job) + ":refs/heads/" + Branch(job) },
        });
    }

    // AC-7d: commits the model made itself on zyggy/<id> (nothing left uncommitted) also stay on zyggy/<id>.
    [Fact]
    public async Task RunAsync_NamedRunFailedAfterModelCommit_ModelCommitsGoToJobBranchOnly()
    {
        // Arrange
        OnOrigin();
        _git.StatusAfterRun = string.Empty;
        ModelEnds("failed");
        var job = await NamedJobAsync();

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        new { Committed = Git("commit").Count, reply.DiffRef, Pushes = Git("push").Select(c => c.Arguments[^1]).ToList() }.Should().BeEquivalentTo(new
        {
            Committed = 0,
            DiffRef = WorktreeGit.TipSha,
            Pushes = new[] { "refs/heads/" + Branch(job) + ":refs/heads/" + Branch(job) },
        });
    }

    // AC-7b: a refused named push (race, ref clash) → failed/git_error; zyggy/<id> pushed under its own name per spec 16.
    [Fact]
    public async Task RunAsync_NamedPushRefused_FailedGitErrorWorkOnJobBranch()
    {
        // Arrange
        Arrange("done-refused");
        var job = await NamedJobAsync();

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        new { reply.Status, reply.Reason, reply.DiffRef, Body = Body(reply), Pushes = Git("push").Select(c => c.Arguments[^1]).ToList() }.Should().BeEquivalentTo(new
        {
            Status = ReportStatus.Failed,
            Reason = "git_error",
            DiffRef = WorktreeGit.TipSha,
            Body = "done\n\nbranch " + Branch(job) + " pushed at " + WorktreeGit.TipSha + "\nbranch " + Name + " not changed: push refused\n",
            Pushes = new[] { "refs/heads/" + Branch(job) + ":" + NamedDestination, "refs/heads/" + Branch(job) + ":refs/heads/" + Branch(job) },
        }, o => o.WithStrictOrdering());
    }

    // AC-7b, AC-11: when origin refuses zyggy/<id> too, it is kept locally with its commits.
    [Fact]
    public async Task RunAsync_NamedPushAndJobBranchPushRefused_JobBranchKeptLocally()
    {
        // Arrange
        _git.Failing.Add("push");
        var job = await NamedJobAsync();

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        new { reply.Status, reply.Reason, reply.DiffRef, Body = Body(reply), Deleted = Git("branch").Any(c => c.Arguments.Contains("-D")) }.Should().BeEquivalentTo(new
        {
            Status = ReportStatus.Failed,
            Reason = "git_error",
            DiffRef = (string?)null,
            Body = "done\n\nbranch " + Branch(job) + " kept locally, not pushed: push\nbranch " + Name + " not changed: push refused\n",
            Deleted = false,
        });
    }

    // AC-7b: no origin → failed/git_error, zyggy/<id> kept locally, nothing pushed.
    [Fact]
    public async Task RunAsync_NamedDoneRunNoOrigin_FailedGitErrorJobBranchKeptNoPush()
    {
        // Arrange
        Arrange("done-no-origin");
        var job = await NamedJobAsync();

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        new { reply.Status, reply.Reason, reply.DiffRef, Body = Body(reply), Pushed = Git("push").Count, Deleted = Git("branch").Count }.Should().BeEquivalentTo(new
        {
            Status = ReportStatus.Failed,
            Reason = "git_error",
            DiffRef = (string?)null,
            Body = "done\n\nbranch " + Branch(job) + " kept locally, not pushed: push\nbranch " + Name + " not changed: no origin\n",
            Pushed = 0,
            Deleted = 0,
        });
    }

    // AC-14, failed fetch (two cases): origin/<branch> absent locally; the named push decides.
    [Theory]
    [InlineData("fetch-fails-refused", ReportStatus.Failed, "git_error")]
    [InlineData("fetch-fails-accepted", ReportStatus.Done, null)]
    public async Task RunAsync_FetchFailedOriginBranchAbsent_NamedPushDecidesTheOutcome(string row, ReportStatus status, string? reason)
    {
        // Arrange
        Arrange(row);
        var job = await NamedJobAsync();

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        new { reply.Status, reply.Reason, Start = Git("worktree").Single(c => c.Arguments.Contains("add")).Arguments[^1] }
            .Should().BeEquivalentTo(new { Status = status, Reason = reason, Start = WorktreeGit.BaseSha });
    }

    // AC-11, AC-14: a failed node commit keeps the worktree and the branch, pushes nothing, and says the named branch is unchanged.
    [Fact]
    public async Task RunAsync_NamedRunCommitFails_WorktreeKeptNothingPushed()
    {
        // Arrange
        Arrange("commit-fails");
        var job = await NamedJobAsync();

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        new { reply.Status, reply.Reason, Body = Body(reply), Pushed = Git("push").Count, Removed = Git("worktree").Count(c => c.Arguments.Contains("remove")) }.Should().BeEquivalentTo(new
        {
            Status = ReportStatus.Failed,
            Reason = "git_error",
            Body = "done\n\nworktree kept at " + WorktreePath(job) + ": commit\nbranch " + Name + " not changed: worktree kept\n",
            Pushed = 0,
            Removed = 0,
        });
    }

    // AC-11, done not ahead: nothing pushed, zyggy/<id> deleted, no branch line (spec 16).
    [Fact]
    public async Task RunAsync_NamedDoneRunNotAhead_DoneNothingPushedJobBranchDeleted()
    {
        // Arrange
        Arrange("done-not-ahead");
        var job = await NamedJobAsync();

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        new { reply.Status, reply.DiffRef, Body = Body(reply), Pushed = Git("push").Count, Deleted = Git("branch").Select(c => c.Arguments[^1]).ToList() }.Should().BeEquivalentTo(new
        {
            Status = ReportStatus.Done,
            DiffRef = (string?)null,
            Body = "done\n\n",
            Pushed = 0,
            Deleted = new[] { Branch(job) },
        });
    }

    // AC-10: no force, no branch-moving command, in any row.
    [Theory]
    [MemberData(nameof(FinishRows))]
    public async Task RunAsync_NamedRunAnyFinishRow_NoForceAndNoBranchMovingCommand(string row)
    {
        // Arrange
        Arrange(row);
        var job = await NamedJobAsync();

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        _h.Processes.Calls.Where(c => c.FileName == "git").Should().NotContain(c =>
            c.Arguments.Any(a => ForceArguments.Contains(a) || a.StartsWith('+'))
            || MovingSubCommands.Contains(RecordingProcessRunner.SubCommand(c)));
    }

    // AC-10, AC-7c: the only command naming the job's branch (besides reading origin/<branch>) is the one push to it.
    [Theory]
    [MemberData(nameof(FinishRows))]
    public async Task RunAsync_NamedRunAnyFinishRow_OnlyTheNamedPushNamesTheBranch(string row)
    {
        // Arrange
        Arrange(row);
        var job = await NamedJobAsync();
        var push = "refs/heads/" + Branch(job) + ":" + NamedDestination;

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        _h.Processes.Calls.SelectMany(c => c.Arguments)
            .Where(a => a.Contains(Name, StringComparison.Ordinal) && a != "refs/remotes/origin/" + Name)
            .Should().BeSubsetOf([push]);
    }

    // AC-10, AC-7d: the named push is issued only after a done run.
    [Theory]
    [MemberData(nameof(FinishRows))]
    public async Task RunAsync_NamedRunAnyFinishRow_NamedPushOnlyAfterADoneRunAhead(string row)
    {
        // Arrange
        Arrange(row);
        var job = await NamedJobAsync();

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        PushedToNamed().Should().Be(row is "done-accepted" or "done-refused" or "fetch-fails-refused" or "fetch-fails-accepted");
    }

    // AC-7d, decided in the build: a failed run with no commits beyond its start pushes nothing (spec 16) and still says the named branch is unchanged.
    [Fact]
    public async Task RunAsync_NamedRunFailedNotAhead_NothingPushedNotChangedLineOnly()
    {
        // Arrange
        Arrange("done-not-ahead");
        ModelEnds("failed");
        var job = await NamedJobAsync();

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        new { reply.Status, reply.DiffRef, Body = Body(reply), Pushed = Git("push").Count }.Should().BeEquivalentTo(new
        {
            Status = ReportStatus.Failed,
            DiffRef = (string?)null,
            Body = RunBody("failed") + "branch " + Name + " not changed: run ended failed\n",
            Pushed = 0,
        });
    }
}
