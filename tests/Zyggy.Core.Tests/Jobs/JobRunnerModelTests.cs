using System.Text;

using NSubstitute;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Models;
using Zyggy.Core.Runs;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Jobs;

/// <summary>
/// Spec 50 slice 3 on the node: a job's model off <c>node.json</c> <c>claude.models</c> is rejected before the model (AC-3); a job with a
/// model asks the model runner to hold it to that model; a mismatch or Claude Code's refusal is reported <c>failed</c> /
/// <c>model_unavailable</c> with the body the spec names (AC-4, AC-5). Never a fallback.
/// </summary>
public sealed class JobRunnerModelTests : IDisposable
{
    private const string Opus = "claude-opus-5-5";

    private readonly ScratchProject _project = ScratchProject.Create();

    private readonly JobRunnerHarness _h = new();

    public void Dispose() => _project.Dispose();

    // AC-3: claude.models set and the job's id not on it: rejected, the model process never started.
    [Fact]
    public async Task RunAsync_ModelNotOnNodeList_RejectedModelUnavailable_ModelNeverStarted()
    {
        // Arrange
        _h.NodePolicy = _h.NodePolicy with { Models = ["claude-fable-5-2"] };
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root, Model: Opus));

        // Act
        JobReply reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        new { reply.Status, reply.Reason, Body = Encoding.UTF8.GetString(reply.Body.Span), Runs = _h.Model.ReceivedCalls().Count() }.Should().BeEquivalentTo(new
        {
            Status = ReportStatus.Rejected,
            Reason = "model_unavailable",
            Body = "model_unavailable: model claude-opus-5-5 is not allowed on this machine\n",
            Runs = 0,
        });
    }

    // AC-3: on the list, or no list at all: the model runs, held to the job's model.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_ModelOnNodeListOrNoList_RunsRequiringTheModel(bool listed)
    {
        // Arrange
        _h.NodePolicy = _h.NodePolicy with { Models = listed ? ["claude-fable-5-2", Opus] : null };
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root, Model: Opus));

        // Act
        JobReply reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        new { reply.Status, _h.Request().Model, _h.Request().RequireModel }.Should().BeEquivalentTo(new { Status = ReportStatus.Done, Model = (string?)Opus, RequireModel = true });
    }

    // A job without a model runs as today, whatever the node's list.
    [Fact]
    public async Task RunAsync_JobWithoutModelNodeListSet_RunsAsToday()
    {
        // Arrange
        _h.NodePolicy = _h.NodePolicy with { Models = ["claude-fable-5-2"] };
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root));

        // Act
        JobReply reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        new { reply.Status, _h.Request().Model, _h.Request().RequireModel }.Should().BeEquivalentTo(new { Status = ReportStatus.Done, Model = (string?)null, RequireModel = false });
    }

    // AC-4: the model runner stopped the run at the system event: failed, model_unavailable, both models named; the report's model is what started.
    [Fact]
    public async Task RunAsync_RunnerReportsMismatch_FailedModelUnavailableNamingBoth()
    {
        // Arrange
        _h.Model.RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(JobRunnerHarness.Failed(RunFailureReason.ModelUnavailable, ModelFailureDetail.ModelMismatch) with { Model = "claude-sonnet-5-5" });
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root, Model: Opus));

        // Act
        JobReply reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        new { reply.Status, reply.Reason, FirstLine = FirstLine(reply), reply.Model }.Should().BeEquivalentTo(new
        {
            Status = ReportStatus.Failed,
            Reason = "model_unavailable",
            FirstLine = "model_unavailable: asked claude-opus-5-5, Claude Code started claude-sonnet-5-5",
            Model = "claude-sonnet-5-5",
        });
    }

    // AC-5: Claude Code refused the model: failed, model_unavailable, the first line of its error in the body.
    [Fact]
    public async Task RunAsync_RunnerReportsRefusal_FailedModelUnavailableWithFirstErrorLine()
    {
        // Arrange
        _h.Model.RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(JobRunnerHarness.Failed(RunFailureReason.ModelUnavailable, ModelFailureDetail.ModelRefused) with { ResultText = "There's an issue with the selected model (claude-opus-0-0).\nsecond line" });
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root, Model: "claude-opus-0-0"));

        // Act
        JobReply reply = await _h.Build().RunAsync(job, CancellationToken.None);

        // Assert
        new { reply.Status, reply.Reason, FirstLine = FirstLine(reply) }.Should().BeEquivalentTo(new
        {
            Status = ReportStatus.Failed,
            Reason = "model_unavailable",
            FirstLine = "model_unavailable: model claude-opus-0-0 refused by Claude Code: There's an issue with the selected model (claude-opus-0-0).",
        });
    }

    private static string FirstLine(JobReply reply) => Encoding.UTF8.GetString(reply.Body.Span).Split('\n')[0];
}
