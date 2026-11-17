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
/// Spec 53 slice 4: the job's <c>description</c> becomes the node's commit subject (body <c>zyggy job &lt;id&gt;</c>) for every outcome,
/// named or not, and a <c>description:</c> report line; a pushed named branch behind <c>origin/&lt;default&gt;</c> gets one "behind" line;
/// the model's prompt never carries the branch or the description (git and model substituted).
/// </summary>
// AC-5, AC-6, AC-13 (unit).
public sealed class WorktreeDescriptionTests : IDisposable
{
    private const string Name = WorktreeNamedBranchTests.Name;

    private const string Description = "Cart: slice 2 # totals";

    private readonly ScratchProject _project = ScratchProject.Create();

    private readonly JobRunnerHarness _h = new();

    private readonly WorktreeGit _git;

    public WorktreeDescriptionTests() => _git = new WorktreeGit(_h.Processes);

    public void Dispose() => _project.Dispose();

    /// <summary>Every run outcome × named or not (AC-5).</summary>
    public static TheoryData<string, bool> OutcomesNamedOrNot() => new()
    {
        { "done", true },
        { "done", false },
        { "failed", true },
        { "failed", false },
        { "timeout", true },
        { "timeout", false },
    };

    private static string Branch(Model job) => "zyggy/" + job.Header.Id.Value;

    private Task<Model> JobAsync(string? branch, string? description, bool worktree = true, EnvelopeId? id = null) =>
        JobRunnerHarness.JobAsync(new JobFields(_project.Root, Worktree: worktree, Branch: branch, Description: description), id: id);

    private List<ProcessSpec> Git(string subCommand) => [.. _h.Processes.CallsOf(subCommand)];

    private static string Body(JobReply reply) => Encoding.UTF8.GetString(reply.Body.Span);

    private void ModelEnds(string outcome)
    {
        if (outcome != "done")
        {
            _h.Model.RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>()).Returns(outcome == "timeout"
                ? JobRunnerHarness.Failed(RunFailureReason.Timeout, "timeout", "partial")
                : JobRunnerHarness.Failed(RunFailureReason.ClaudeError, "exit 1", "partial"));
        }
    }

    // AC-5: with a description the node's commit has it as subject and "zyggy job <id>" as body, for every outcome, named or not.
    [Theory]
    [MemberData(nameof(OutcomesNamedOrNot))]
    public async Task RunAsync_WithDescription_CommitSubjectIsDescriptionBodyIsJobLine(string outcome, bool named)
    {
        // Arrange
        ModelEnds(outcome);
        var job = await JobAsync(named ? Name : null, Description);

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        Git("commit").Single().StandardInput.Should().Be(Description + "\n\nzyggy job " + job.Header.Id.Value + "\n");
    }

    // AC-5: without a description a named job's commit message stays "zyggy job <id>".
    [Fact]
    public async Task RunAsync_NamedWithoutDescription_CommitMessageIsJobLine()
    {
        // Arrange
        var job = await JobAsync(Name, null);

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        Git("commit").Single().StandardInput.Should().Be("zyggy job " + job.Header.Id.Value + "\n");
    }

    // AC-6: a pushed named branch → its pushed-at line, then the description line; not behind → no behind line.
    [Fact]
    public async Task RunAsync_NamedDonePushedWithDescription_BranchLineThenDescriptionLine()
    {
        // Arrange
        var job = await JobAsync(Name, Description);

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        Body(reply).Should().Be("done\n\nbranch " + Name + " pushed at " + WorktreeGit.TipSha + "\ndescription: " + Description + "\n");
    }

    // AC-6: a pushed named branch behind origin/<default> → one behind line after the description line.
    [Fact]
    public async Task RunAsync_NamedDonePushedBehindDefault_BehindLineAfterDescription()
    {
        // Arrange
        _git.Behind = 3;
        var job = await JobAsync(Name, Description);

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        Body(reply).Should().Be("done\n\nbranch " + Name + " pushed at " + WorktreeGit.TipSha + "\ndescription: " + Description
            + "\nbranch " + Name + " is 3 commits behind origin/main\n");
    }

    // AC-6: the behind count compares the pushed tip with origin/<default> (commits on the default branch the tip lacks).
    [Fact]
    public async Task RunAsync_NamedDonePushed_BehindCountedFromTipToOriginDefault()
    {
        // Arrange
        var job = await JobAsync(Name, null);

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        Git("rev-list").Select(c => c.Arguments[^1]).Should().Contain(WorktreeGit.TipSha + "..refs/remotes/origin/main");
    }

    // AC-6: a behind line without a description stands right after the pushed-at line.
    [Fact]
    public async Task RunAsync_NamedDonePushedBehindNoDescription_BehindLineAfterBranchLine()
    {
        // Arrange
        _git.Behind = 1;
        var job = await JobAsync(Name, null);

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        Body(reply).Should().Be("done\n\nbranch " + Name + " pushed at " + WorktreeGit.TipSha + "\nbranch " + Name + " is 1 commits behind origin/main\n");
    }

    // AC-6: the behind line is written only when the named branch was pushed (a refused push writes none).
    [Fact]
    public async Task RunAsync_NamedPushRefusedBehindDefault_NoBehindLine()
    {
        // Arrange
        _git.Behind = 2;
        _git.RefusedPushDestinations.Add("refs/heads/" + Name);
        var job = await JobAsync(Name, Description);

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        Body(reply).Should().Be("done\n\nbranch " + Branch(job) + " pushed at " + WorktreeGit.TipSha + "\nbranch " + Name + " not changed: push refused\ndescription: "
            + Description + "\n");
    }

    // AC-6: an unnamed job with a description → spec 16's branch line, then the description line; never a behind line.
    [Fact]
    public async Task RunAsync_UnnamedWithDescription_JobBranchLineThenDescriptionLine()
    {
        // Arrange
        _git.Behind = 4;
        var job = await JobAsync(null, Description);

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        Body(reply).Should().Be("done\n\nbranch " + Branch(job) + " pushed at " + WorktreeGit.TipSha + "\ndescription: " + Description + "\n");
    }

    // AC-6: nothing pushed (no commits beyond the start) → the description line is still written.
    [Fact]
    public async Task RunAsync_NamedDoneNothingPushed_DescriptionLineOnly()
    {
        // Arrange
        _git.StatusAfterRun = string.Empty;
        _git.Ahead = 0;
        var job = await JobAsync(Name, Description);

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        Body(reply).Should().Be("done\n\ndescription: " + Description + "\n");
    }

    // AC-6: a failed named run → 16's line, the not-changed line, then the description line.
    [Fact]
    public async Task RunAsync_NamedRunFailedWithDescription_DescriptionAfterNotChangedLine()
    {
        // Arrange
        ModelEnds("failed");
        var job = await JobAsync(Name, Description);

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        Body(reply).Should().Be("claude_error: exit 1\n\npartial\nbranch " + Branch(job) + " pushed at " + WorktreeGit.TipSha + "\nbranch " + Name
            + " not changed: run ended failed\ndescription: " + Description + "\n");
    }

    // AC-6: a job without branch and description keeps today's body exactly.
    [Fact]
    public async Task RunAsync_NoBranchNoDescription_BodyAsToday()
    {
        // Arrange
        _git.Behind = 5;
        var job = await JobAsync(null, null);

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        Body(reply).Should().Be("done\n\nbranch " + Branch(job) + " pushed at " + WorktreeGit.TipSha + "\n");
    }

    // AC-13: the prompt for a job with branch and description equals, byte for byte, the prompt for the same job without them.
    [Fact]
    public async Task RunAsync_BranchAndDescription_PromptIdenticalToJobWithoutThem()
    {
        // Arrange
        var id = EnvelopeId.Parse("01M7N60Y2CPMDXBKYTCDR21JJA");
        var withFields = await JobRunnerHarness.JobAsync(
            new JobFields(_project.Root, Worktree: true, Branch: "feature/ac13-marker", Description: "ac13 description marker"), id: id);
        var without = await JobRunnerHarness.JobAsync(new JobFields(_project.Root, Worktree: true), id: id);
        var runner = _h.Build();
        await runner.RunAsync(without, CancellationToken.None);

        // Act
        await runner.RunAsync(withFields, CancellationToken.None);

        // Assert
        var prompts = _h.Model.ReceivedCalls().Where(c => c.GetMethodInfo().Name == nameof(IModelRunner.RunAsync))
            .Select(c => Encoding.UTF8.GetBytes(((ModelRunRequest)c.GetArguments()[0]!).Prompt)).ToList();
        prompts[1].Should().Equal(prompts[0]);
    }

    // AC-13: the prompt carries neither the branch name nor the description text.
    [Fact]
    public async Task RunAsync_BranchAndDescription_PromptCarriesNeither()
    {
        // Arrange
        var job = await JobAsync("feature/ac13-marker", "ac13 description marker");

        // Act
        await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        _h.Request().Prompt.Should().NotContainAny("feature/ac13-marker", "ac13 description marker");
    }

    // AC-6 (decided in the build): a project-directory job (worktree: false) with a description also gets the description line.
    [Fact]
    public async Task RunAsync_NonWorktreeJobWithDescription_DescriptionLine()
    {
        // Arrange
        var job = await JobAsync(null, Description, worktree: false);

        // Act
        var reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        Body(reply).Should().Be("done\n\ndescription: " + Description + "\n");
    }
}
