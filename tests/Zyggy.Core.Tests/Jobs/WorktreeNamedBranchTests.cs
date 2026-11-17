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
/// Spec 53 slice 3a: a named worktree job runs on <c>zyggy/&lt;id&gt;</c> started at <c>origin/&lt;branch&gt;</c> (else the default tip),
/// and only a <c>done</c> run is pushed to the named branch on <c>origin</c>, by one non-forced push; the local named branch is never read
/// or written (git and model substituted).
/// </summary>
// AC-3, AC-4, AC-7, AC-7c, AC-11 (accepted-push row), AC-12 (unit).
public sealed class WorktreeNamedBranchTests : IDisposable
{
    public const string Name = "feature/12-cart";

    public const string OriginNamedSha = "3333333333333333333333333333333333333333";

    private readonly ScratchProject _project = ScratchProject.Create();

    private readonly JobRunnerHarness _h = new();

    private readonly WorktreeGit _git;

    public WorktreeNamedBranchTests() => _git = new WorktreeGit(_h.Processes);

    public void Dispose() => _project.Dispose();

    /// <summary>The local states of an owner's branch named like the job's branch (AC-4, AC-7c); <c>none</c> = absent.</summary>
    public static TheoryData<string?> LocalStates() => new()
    {
        { (string?)null },
        { "4444444444444444444444444444444444444444" },
        { "5555555555555555555555555555555555555555" },
        { "6666666666666666666666666666666666666666" },
    };

    private string WorktreePath(Model job) => Path.Join(_h.Options.NodeDir, "worktrees", "acme", job.Header.Id.Value);

    private static string Branch(Model job) => "zyggy/" + job.Header.Id.Value;

    private Task<Model> NamedJobAsync() => JobRunnerHarness.JobAsync(new JobFields(_project.Root, Worktree: true, Branch: Name));

    private void OnOrigin() => _git.Refs["refs/remotes/origin/" + Name] = OriginNamedSha;

    private void LocalNamed(string? sha)
    {
        if (sha is not null)
        {
            _git.Refs["refs/heads/" + Name] = sha;
        }
    }

    private List<ProcessSpec> Git(string subCommand) => [.. _h.Processes.CallsOf(subCommand)];

    private int IndexOf(Func<ProcessSpec, bool> predicate) => _h.Processes.Calls.FindIndex(c => predicate(c));

    private static bool Is(ProcessSpec spec, params string[] arguments) => arguments.All(spec.Arguments.Contains);

    private static string Body(JobReply reply) => Encoding.UTF8.GetString(reply.Body.Span);

    private void ModelEnds(string outcome) =>
        _h.Model.RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>()).Returns(outcome == "timeout"
            ? JobRunnerHarness.Failed(RunFailureReason.Timeout, "timeout", "partial")
            : JobRunnerHarness.Failed(RunFailureReason.ClaudeError, "exit 1", "partial"));

    // AC-3: not on origin → zyggy/<id> at the default tip.
    [Fact]
    public async Task RunAsync_NamedBranchNotOnOrigin_WorktreeOnJobBranchAtDefaultTip()
    {
        // Arrange
        var job = await NamedJobAsync();

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        Git("worktree").Single(c => Is(c, "add")).Arguments.Should().Equal("worktree", "add", "-b", Branch(job), WorktreePath(job), WorktreeGit.BaseSha);
    }

    // AC-3: no origin/<default> → the local default branch is the start.
    [Fact]
    public async Task RunAsync_NamedBranchNotOnOriginNoOriginDefault_StartsAtLocalDefault()
    {
        // Arrange
        _git.OriginMainExists = false;
        var job = await NamedJobAsync();

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        new { Resolved = Git("rev-parse").Any(c => c.Arguments[^1] == "refs/heads/main"), Start = Git("worktree").Single(c => Is(c, "add")).Arguments[^1] }
            .Should().BeEquivalentTo(new { Resolved = true, Start = WorktreeGit.BaseSha });
    }

    // AC-4: on origin → start at origin/<branch>, whatever the local branch of that name is.
    [Theory]
    [MemberData(nameof(LocalStates))]
    public async Task RunAsync_NamedBranchOnOrigin_StartsAtOriginBranchWhateverTheLocalBranch(string? localSha)
    {
        // Arrange
        OnOrigin();
        LocalNamed(localSha);
        var job = await NamedJobAsync();

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        Git("worktree").Single(c => Is(c, "add")).Arguments.Should().Equal("worktree", "add", "-b", Branch(job), WorktreePath(job), OriginNamedSha);
    }

    // AC-4: the start point is read after the fetch, so a fetched origin/<branch> counts.
    [Fact]
    public async Task RunAsync_NamedBranch_OriginBranchResolvedAfterTheFetch()
    {
        // Arrange
        OnOrigin();
        var job = await NamedJobAsync();

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        IndexOf(c => Is(c, "fetch")).Should().BeLessThan(IndexOf(c => c.Arguments[^1] == "refs/remotes/origin/" + Name));
    }

    // AC-3, AC-10: a done run → exactly one push, zyggy/<id> to the named branch, no force.
    [Fact]
    public async Task RunAsync_NamedDoneRunAhead_OnePushOfJobBranchToNamedBranchNoForce()
    {
        // Arrange
        var job = await NamedJobAsync();

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        Git("push").Select(c => c.Arguments).Should().ContainSingle()
            .Which.Should().Equal("push", "origin", "refs/heads/" + Branch(job) + ":refs/heads/" + Name);
    }

    [Fact]
    public async Task RunAsync_NamedDoneRunAhead_DoneDiffRefTipAndPushedAtLine()
    {
        // Arrange
        OnOrigin();
        var job = await NamedJobAsync();

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        new { reply.Status, reply.Reason, reply.DiffRef, Body = Body(reply) }.Should().BeEquivalentTo(new
        {
            Status = ReportStatus.Done,
            Reason = (string?)null,
            DiffRef = WorktreeGit.TipSha,
            Body = "done\n\nbranch " + Name + " pushed at " + WorktreeGit.TipSha + "\n",
        });
    }

    // AC-7: files and ahead compare the start tip (origin/<branch>) with the final tip.
    [Fact]
    public async Task RunAsync_NamedBranchOnOrigin_FilesAndAheadComparedWithOriginBranchTip()
    {
        // Arrange
        OnOrigin();
        var job = await NamedJobAsync();

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        new
        {
            Ahead = Git("rev-list").Single().Arguments[^1],
            Diff = Git("diff").Single().Arguments[^1],
        }.Should().BeEquivalentTo(new { Ahead = OriginNamedSha + ".." + WorktreeGit.TipSha, Diff = OriginNamedSha + "..." + WorktreeGit.TipSha });
    }

    // AC-11, accepted-push row: worktree removed, then the local zyggy/<id> deleted.
    [Fact]
    public async Task RunAsync_NamedPushAccepted_JobBranchDeletedAfterWorktreeRemoved()
    {
        // Arrange
        var job = await NamedJobAsync();

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        IndexOf(c => Is(c, "worktree", "remove")).Should().BeInRange(0, IndexOf(c => c.Arguments.SequenceEqual(new[] { "branch", "-D", Branch(job) })) - 1);
    }

    // AC-11: git refuses to delete a branch a worktree still holds; the next run of this job id cleans both (spec 16).
    [Fact]
    public async Task RunAsync_NamedPushAcceptedRemoveFails_JobBranchNotDeletedStillDone()
    {
        // Arrange
        _git.Failing.Add("worktree remove");
        var job = await NamedJobAsync();

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        new { reply.Status, Deleted = Git("branch").Any(c => Is(c, "-D")) }.Should().BeEquivalentTo(new { Status = ReportStatus.Done, Deleted = false });
    }

    // AC-7c: the local branch named like the job's branch is never named by any git command; only the push destination names it.
    [Theory]
    [MemberData(nameof(LocalStates))]
    public async Task RunAsync_NamedDoneRun_LocalNamedBranchNeverNamedExceptAsPushDestination(string? localSha)
    {
        // Arrange
        OnOrigin();
        LocalNamed(localSha);
        var job = await NamedJobAsync();
        var push = "refs/heads/" + Branch(job) + ":refs/heads/" + Name;

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        _h.Processes.Calls.SelectMany(c => c.Arguments)
            .Where(a => a.Contains(Name, StringComparison.Ordinal) && a != push && a != "refs/remotes/origin/" + Name)
            .Should().BeEmpty();
    }

    // Test-first order (c): a failed or timed-out run never issues the named push.
    [Theory]
    [InlineData("failed")]
    [InlineData("timeout")]
    public async Task RunAsync_NamedRunNotDone_NamedBranchNeverPushed(string outcome)
    {
        // Arrange
        OnOrigin();
        ModelEnds(outcome);
        var job = await NamedJobAsync();

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        Git("push").Select(c => c.Arguments[^1]).Should().NotContain(a => a.EndsWith(":refs/heads/" + Name, StringComparison.Ordinal));
    }

    // AC-12: an unnamed job is unchanged — zyggy/<id> pushed under its own name.
    [Fact]
    public async Task RunAsync_UnnamedDoneRun_PushesJobBranchUnderItsOwnName()
    {
        // Arrange
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root, Worktree: true));

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        Git("push").Select(c => c.Arguments[^1]).Should().Equal("refs/heads/" + Branch(job) + ":refs/heads/" + Branch(job));
    }

    // AC-12: a description without a branch changes nothing about where the work goes.
    [Fact]
    public async Task RunAsync_DescriptionWithoutBranch_PushesJobBranchUnderItsOwnName()
    {
        // Arrange
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root, Worktree: true, Description: "Cart: slice 1"));

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        Git("push").Select(c => c.Arguments[^1]).Should().Equal("refs/heads/" + Branch(job) + ":refs/heads/" + Branch(job));
    }
}
