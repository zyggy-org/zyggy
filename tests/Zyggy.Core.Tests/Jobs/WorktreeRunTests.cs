using System.Text;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Zyggy.Core.Envelope;
using Zyggy.Core.Processes;
using Zyggy.Core.Runs;
using Zyggy.Core.Tests.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Jobs;

/// <summary>Spec 16 slice 1: a <c>worktree: true</c> job runs in its own worktree and its report names the pushed commit and the changed files (git and model substituted).</summary>
public sealed class WorktreeRunTests : IDisposable
{
    private readonly ScratchProject _project = ScratchProject.Create();

    private readonly JobRunnerHarness _h = new();

    private readonly WorktreeGit _git;

    public WorktreeRunTests() => _git = new WorktreeGit(_h.Processes);

    public void Dispose() => _project.Dispose();

    private string WorktreePath(Model job) => Path.Join(_h.Options.NodeDir, "worktrees", "acme", job.Header.Id.Value);

    private static string Branch(Model job) => "zyggy/" + job.Header.Id.Value;

    private Task<Model> WorktreeJobAsync() => JobRunnerHarness.JobAsync(new JobFields(_project.Root, Worktree: true));

    private List<ProcessSpec> Git(string subCommand) => [.. _h.Processes.CallsOf(subCommand)];

    private int IndexOf(Func<ProcessSpec, bool> predicate) => _h.Processes.Calls.FindIndex(c => predicate(c));

    private static bool Is(ProcessSpec spec, params string[] arguments) => arguments.All(spec.Arguments.Contains);

    [Fact]
    public async Task RunAsync_WorktreeJob_ModelRunsInWorktreeFolder()
    {
        // Arrange
        var job = await WorktreeJobAsync();

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        _h.Request().WorkingDirectory.Should().Be(WorktreePath(job));
    }

    [Fact]
    public async Task RunAsync_WorktreeJob_PromptNamesWorktreeAsProjectPath()
    {
        // Arrange
        var job = await WorktreeJobAsync();

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        _h.Request().Prompt.Should().Contain(WorktreePath(job)).And.NotContain(_project.Root);
    }

    [Fact]
    public async Task RunAsync_OriginMainExists_AddsNewBranchFromOriginMainInProject()
    {
        // Arrange
        var job = await WorktreeJobAsync();

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        Git("worktree").Should().ContainSingle(c => Is(c, "add"))
            .Which.Should().Match<ProcessSpec>(c =>
                c.WorkingDirectory == _project.Root
                && c.Arguments.SequenceEqual(new[] { "worktree", "add", "-b", Branch(job), WorktreePath(job), WorktreeGit.BaseSha }));
    }

    [Fact]
    public async Task RunAsync_NoOriginMain_BranchesFromLocalMain()
    {
        // Arrange
        _git.OriginMainExists = false;
        var job = await WorktreeJobAsync();

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        Git("rev-parse").Select(c => c.Arguments[^1]).Should().Contain("refs/heads/main");
    }

    [Fact]
    public async Task RunAsync_OriginExists_FetchesBeforeWorktreeAdd()
    {
        // Arrange
        var job = await WorktreeJobAsync();

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        IndexOf(c => Is(c, "fetch", "origin")).Should().BeInRange(0, IndexOf(c => Is(c, "worktree", "add")) - 1);
    }

    [Fact]
    public async Task RunAsync_ExistingJobBranch_ReusedWithoutDashB()
    {
        // Arrange
        _git.JobBranchExists = true;
        var job = await WorktreeJobAsync();

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        Git("worktree").Single(c => Is(c, "add")).Arguments.Should().Equal("worktree", "add", WorktreePath(job), Branch(job));
    }

    [Fact]
    public async Task RunAsync_LeftoverChanges_CommittedWithMachineIdentityAndHooksSkipped()
    {
        // Arrange
        var job = await WorktreeJobAsync();

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        Git("commit").Should().ContainSingle().Which.Should().Match<ProcessSpec>(c =>
            c.WorkingDirectory == WorktreePath(job)
            && Is(c, "user.name=zyggy (home-laptop)", "user.email=home-laptop@zyggy.org", "commit.gpgsign=false", "--no-verify")
            && c.StandardInput == "zyggy job " + job.Header.Id.Value + "\n");
    }

    [Fact]
    public async Task RunAsync_LeftoverChanges_StagedWithAddAllBeforeCommit()
    {
        // Arrange
        var job = await WorktreeJobAsync();

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        IndexOf(c => Is(c, "add", "-A")).Should().BeInRange(0, IndexOf(c => RecordingProcessRunner.SubCommand(c) == "commit") - 1);
    }

    [Fact]
    public async Task RunAsync_CleanWorktree_NoNodeCommit()
    {
        // Arrange
        _git.StatusAfterRun = string.Empty;
        var job = await WorktreeJobAsync();

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        Git("commit").Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_WorktreeJob_NeverWritesGitConfig()
    {
        // Arrange
        var job = await WorktreeJobAsync();

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        Git("config").Should().OnlyContain(c => c.Arguments.Contains("--get"));
    }

    [Fact]
    public async Task RunAsync_BranchAhead_CommitThenPushThenRemoveThenDeleteBranch()
    {
        // Arrange
        var job = await WorktreeJobAsync();

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        int[] order =
        [
            IndexOf(c => RecordingProcessRunner.SubCommand(c) == "commit"),
            IndexOf(c => RecordingProcessRunner.SubCommand(c) == "push"),
            IndexOf(c => Is(c, "worktree", "remove")),
            IndexOf(c => Is(c, "branch", "-D", Branch(job))),
        ];
        order.Should().NotContain(-1).And.BeInAscendingOrder();
    }

    [Fact]
    public async Task RunAsync_BranchAhead_PushesJobBranchToOriginWithoutForce()
    {
        // Arrange
        var job = await WorktreeJobAsync();
        var refspec = "refs/heads/" + Branch(job) + ":refs/heads/" + Branch(job);

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        Git("push").Should().ContainSingle().Which.Arguments.Should().Equal("push", "origin", refspec);
    }

    [Fact]
    public async Task RunAsync_BranchPushed_ReplyDoneWithDiffRefAndResultLine()
    {
        // Arrange
        var job = await WorktreeJobAsync();

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        new { reply.Status, reply.DiffRef, Body = Encoding.UTF8.GetString(reply.Body.Span) }.Should().BeEquivalentTo(new
        {
            Status = ReportStatus.Done,
            DiffRef = WorktreeGit.TipSha,
            Body = "done\n\nbranch " + Branch(job) + " pushed at " + WorktreeGit.TipSha + "\n",
        });
    }

    [Fact]
    public async Task RunAsync_ChangedPaths_SortedOrdinally()
    {
        // Arrange
        _git.ChangedPaths = ["src/b.cs", "README.md", "src/a.cs", "docs/x.md"];
        var job = await WorktreeJobAsync();

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        reply.FilesChanged.Should().Equal("README.md", "docs/x.md", "src/a.cs", "src/b.cs");
    }

    [Fact]
    public async Task RunAsync_ChangedPaths_DiffedBaseToTipWithoutRenames()
    {
        // Arrange
        var job = await WorktreeJobAsync();

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        Git("diff").Should().ContainSingle().Which.Arguments.Should()
            .Equal("diff", "--name-only", "-z", "--no-renames", WorktreeGit.BaseSha + "..." + WorktreeGit.TipSha);
    }

    [Fact]
    public async Task RunAsync_MoreThan200ChangedPaths_First200AndTotalInBody()
    {
        // Arrange
        _git.ChangedPaths = [.. Enumerable.Range(0, 250).Select(i => $"f/{i:D3}.txt").Reverse()];
        var job = await WorktreeJobAsync();

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        new { First = reply.FilesChanged![0], Last = reply.FilesChanged[^1], reply.FilesChanged.Count, Total = Encoding.UTF8.GetString(reply.Body.Span).Contains("files_changed: first 200 of 250", StringComparison.Ordinal) }
            .Should().BeEquivalentTo(new { First = "f/000.txt", Last = "f/199.txt", Count = 200, Total = true });
    }

    [Fact]
    public async Task RunAsync_NothingChanged_DoneNoFilesNoDiffRefNoPush()
    {
        // Arrange
        _git.StatusAfterRun = string.Empty;
        _git.Ahead = 0;
        _git.ChangedPaths = [];
        var job = await WorktreeJobAsync();

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        new { reply.Status, reply.FilesChanged, reply.DiffRef, Pushes = Git("push").Count, Body = Encoding.UTF8.GetString(reply.Body.Span) }
            .Should().BeEquivalentTo(new { Status = ReportStatus.Done, FilesChanged = Array.Empty<string>(), DiffRef = (string?)null, Pushes = 0, Body = "done\n\n" });
    }

    [Fact]
    public async Task RunAsync_NothingChanged_WorktreeRemovedAndBranchDeleted()
    {
        // Arrange
        _git.StatusAfterRun = string.Empty;
        _git.Ahead = 0;
        var job = await WorktreeJobAsync();

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        new { Removed = IndexOf(c => Is(c, "worktree", "remove", WorktreePath(job))) >= 0, Deleted = IndexOf(c => Is(c, "branch", "-D", Branch(job))) >= 0 }
            .Should().BeEquivalentTo(new { Removed = true, Deleted = true });
    }

    [Fact]
    public async Task RunAsync_InPlaceJob_NoWorktreeOrBranchCommand()
    {
        // Arrange
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root));

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert: pre-flight's rev-parse is the only git call (the hook self-test of spec 15 is not git)
        _h.Processes.Calls.Where(c => c.FileName == "git").Select(RecordingProcessRunner.SubCommand).Should().Equal("rev-parse");
    }

    [Fact]
    public async Task RunAsync_InPlaceJob_NoFilesNoDiffRef()
    {
        // Arrange
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root));

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        new { reply.FilesChanged, reply.DiffRef, Body = Encoding.UTF8.GetString(reply.Body.Span) }
            .Should().BeEquivalentTo(new { FilesChanged = Array.Empty<string>(), DiffRef = (string?)null, Body = "done\n\n" });
    }

    [Fact]
    public async Task RunAsync_WorktreeJob_NoRunningInPlaceWarning()
    {
        // Arrange
        var job = await WorktreeJobAsync();

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        _h.Log.AtLevel(LogLevel.Warning).Should().NotContain(e => e.Message.Contains("running in place", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_ModelTimedOut_FinishStillCommitsAndPushes()
    {
        // Arrange
        _h.Model.RunAsync(Arg.Any<Zyggy.Core.Models.ModelRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(JobRunnerHarness.Failed(RunFailureReason.Timeout, "timeout", "partial"));
        var job = await WorktreeJobAsync();

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        new { reply.Status, reply.DiffRef, Commits = Git("commit").Count, Pushes = Git("push").Count }
            .Should().BeEquivalentTo(new { Status = ReportStatus.Timeout, DiffRef = WorktreeGit.TipSha, Commits = 1, Pushes = 1 });
    }
}
