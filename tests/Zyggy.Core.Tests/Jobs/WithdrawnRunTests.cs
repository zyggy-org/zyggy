using System.Text;

using NSubstitute;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Models;
using Zyggy.Core.Runs;
using Zyggy.Core.Tests.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Jobs;

/// <summary>
/// Spec 61 slice 5 (AC-16, the runner's half): a running job that is withdrawn is stopped like a run at its limit — the model's process
/// tree is killed through the request's limit signal — and the run ends <c>failed</c> / <c>withdrawn</c> with the text the model had
/// produced, never <c>timeout</c>. A run that is not withdrawn is untouched by the signal of another job.
/// </summary>
public sealed class WithdrawnRunTests : IDisposable
{
    // A guard against a hang when the behaviour is broken: never reached by a passing test.
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(5);

    private readonly ScratchProject _project = ScratchProject.Create();

    private readonly JobRunnerHarness _h = new();

    private readonly TaskCompletionSource<ModelRunRequest> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TaskCompletionSource _finish = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public WithdrawnRunTests() =>
        _h.Model.RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>()).Returns(call => RunModelAsync(call.Arg<ModelRunRequest>()));

    public void Dispose() => _project.Dispose();

    // AC-16: the withdrawal stops the model and the run ends failed / withdrawn with the model's partial text.
    [Fact]
    public async Task RunAsync_WithdrawnWhileTheModelRuns_FailedWithdrawnWithPartialText()
    {
        // Arrange
        Model job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root, TimeoutMinutes: 150));
        Task<JobReply> run = _h.Build().RunAsync(job, CancellationToken.None);
        await _started.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);

        // Act
        _h.Withdrawals.Withdraw(job.Header.Id);

        // Assert
        JobReply reply = await run.WaitAsync(Guard, TestContext.Current.CancellationToken);
        new { reply.Status, reply.Reason, Body = Encoding.UTF8.GetString(reply.Body.Span) }
            .Should().BeEquivalentTo(new { Status = ReportStatus.Failed, Reason = "withdrawn", Body = "Looking at the logs…\n" });
    }

    // AC-16: a withdrawal raised before the run asked for its signal still stops it (the listener may act between claim and model start).
    [Fact]
    public async Task RunAsync_WithdrawnBeforeTheModelStarted_FailedWithdrawn()
    {
        // Arrange
        Model job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root, TimeoutMinutes: 150));
        _h.Withdrawals.Withdraw(job.Header.Id);

        // Act
        JobReply reply = await _h.Build().RunAsync(job, CancellationToken.None).WaitAsync(Guard, TestContext.Current.CancellationToken);

        // Assert
        new { reply.Status, reply.Reason }.Should().BeEquivalentTo(new { Status = ReportStatus.Failed, Reason = "withdrawn" });
    }

    // The signal is per job: withdrawing another id leaves this run alone, and it finishes done.
    [Fact]
    public async Task RunAsync_AnotherJobWithdrawn_RunFinishesDone()
    {
        // Arrange
        Model job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root, TimeoutMinutes: 150));
        Task<JobReply> run = _h.Build().RunAsync(job, CancellationToken.None);
        await _started.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);
        _h.Withdrawals.Withdraw(EnvelopeId.Parse("01M87D0000KKKKKKKKKKKKKKKK"));

        // Act
        _finish.SetResult();

        // Assert
        (await run.WaitAsync(Guard, TestContext.Current.CancellationToken)).Status.Should().Be(ReportStatus.Done);
    }

    // A closed signal starts unsignalled: a later run of the same id (re-run after a failure) is not withdrawn by an old signal.
    [Fact]
    public async Task RunAsync_SignalClosedAfterAnEarlierRun_NotWithdrawn()
    {
        // Arrange
        Model job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root, TimeoutMinutes: 150));
        _h.Withdrawals.Withdraw(job.Header.Id);
        _h.Withdrawals.Close(job.Header.Id);

        // Act
        Task<JobReply> run = _h.Build().RunAsync(job, CancellationToken.None);
        ModelRunRequest request = await _started.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);
        bool stopped = request.LimitReached.IsCancellationRequested;
        _finish.SetResult();
        await run.WaitAsync(Guard, TestContext.Current.CancellationToken);

        // Assert
        stopped.Should().BeFalse("the earlier run's signal was closed");
    }

    // The model: runs until the test finishes it (done) or the run's limit signal fires (timeout with its partial text, as the CLI runner answers).
    private async Task<ModelRunResult> RunModelAsync(ModelRunRequest request)
    {
        _started.TrySetResult(request);
        var limit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = request.LimitReached.Register(() => limit.TrySetResult());
        var ended = await Task.WhenAny(_finish.Task, limit.Task).WaitAsync(Guard).ConfigureAwait(false);
        return ended == _finish.Task
            ? JobRunnerHarness.Succeeded("done")
            : JobRunnerHarness.Failed(RunFailureReason.Timeout, "timeout", "Looking at the logs…");
    }
}
