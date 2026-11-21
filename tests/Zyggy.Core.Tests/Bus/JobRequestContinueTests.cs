using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;

namespace Zyggy.Core.Tests.Bus;

/// <summary>
/// Spec 59 slice 6 (AC-9, unit side): the <c>--continue</c> and <c>--max-turns</c> options of <c>zyggy submit</c> are checked before
/// signing — <c>--continue</c> needs <c>--worktree</c> and a job id, <c>--max-turns</c> is at least 1, and neither goes on a fetch or
/// command job.
/// </summary>
public sealed class JobRequestContinueTests
{
    private const string Earlier = "01J8Y3N7Q2X9Z4A5B6C7D8E9F1";

    [Fact]
    public void TryWithContinue_WithWorktree_SetsBoth()
    {
        // Arrange
        JobRequest request = Request(worktree: true);

        // Act
        JobRequestError? error = request.TryWithContinue(Earlier, 20, out JobRequest? continued);

        // Assert
        new { error, continued!.Continue, continued.MaxTurns }.Should().BeEquivalentTo(new { error = (JobRequestError?)null, Continue = EnvelopeId.Parse(Earlier), MaxTurns = 20 });
    }

    [Theory]
    [InlineData(false, Earlier, 20, "continue")]
    [InlineData(true, "not-an-id", 20, "continue")]
    [InlineData(true, Earlier, 0, "max_turns")]
    [InlineData(false, null, -1, "max_turns")]
    public void TryWithContinue_OutOfShape_Refused(bool worktree, string? continueId, int maxTurns, string field)
    {
        // Arrange
        JobRequest request = Request(worktree);

        // Act
        JobRequestError? error = request.TryWithContinue(continueId, maxTurns, out JobRequest? continued);

        // Assert
        new { error?.Field, Continued = continued }.Should().BeEquivalentTo(new { Field = field, Continued = (JobRequest?)null });
    }

    [Fact]
    public void TryWithFetch_AfterMaxTurns_Refused()
    {
        // Arrange
        Request(worktree: false).TryWithContinue(null, 20, out JobRequest? limited).Should().BeNull();

        // Act
        JobRequestError? error = limited!.TryWithFetch("docs/plan.md", null, out JobRequest? fetch);

        // Assert
        new { error?.Field, Fetch = fetch }.Should().BeEquivalentTo(new { Field = "max_turns", Fetch = (JobRequest?)null });
    }

    [Fact]
    public void TryWithCommand_AfterMaxTurns_Refused()
    {
        // Arrange
        Request(worktree: false).TryWithContinue(null, 20, out JobRequest? limited).Should().BeNull();

        // Act
        JobRequestError? error = limited!.TryWithCommand("build", out JobRequest? command);

        // Assert
        new { error?.Field, Command = command }.Should().BeEquivalentTo(new { Field = "max_turns", Command = (JobRequest?)null });
    }

    private static JobRequest Request(bool worktree)
    {
        JobRequest.TryCreate("home-laptop", "shop", null, null, null, null, "Go on.\n", out JobRequest? request).Should().BeNull();
        return request! with { Worktree = worktree };
    }
}
