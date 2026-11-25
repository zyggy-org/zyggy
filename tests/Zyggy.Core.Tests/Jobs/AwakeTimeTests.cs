using System.Text;

using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Jobs;
using Zyggy.Core.Models;
using Zyggy.Core.Runs;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Jobs;

/// <summary>
/// Spec 61 slice 3 (AC-7, AC-8, AC-9): a run's limit counts awake time, a run ends at 24 hours of wall-clock age, and the report says how
/// long the machine slept. The job runner over a model substitute that waits for the run's limit; the machine's sleep is a
/// <see cref="SleepingClock"/>: its wall clock jumps while its timers stand still, as a suspended machine's do.
/// </summary>
public sealed class AwakeTimeTests : IDisposable
{
    // A guard against a hang when the behaviour is broken: never reached by a passing test.
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(20);

    private readonly ScratchProject _project = ScratchProject.Create();

    private readonly JobRunnerHarness _h = new();

    private readonly SleepingClock _clock;

    private readonly TaskCompletionSource<ModelRunRequest> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TaskCompletionSource _finish = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public AwakeTimeTests()
    {
        _clock = new SleepingClock(_h.Clock);
        _h.ClockOverride = _clock;
        _h.Model.RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>()).Returns(call => RunModelAsync(call.Arg<ModelRunRequest>()));
    }

    public void Dispose() => _project.Dispose();

    // AC-7: a 9-hour jump between two notes is sleep: the 150-minute run is not ended at the jump.
    [Fact]
    public async Task RunAsync_NineHourJumpBetweenTwoNotes_NotEndedAtTheJump()
    {
        // Arrange
        ModelRunRequest request = await StartAsync(150);
        AwakeFor(TimeSpan.FromMinutes(60));
        _clock.Sleep(TimeSpan.FromHours(9));

        // Act
        AwakeFor(TimeSpan.FromSeconds(30));

        // Assert
        request.LimitReached.IsCancellationRequested.Should().BeFalse("60 awake minutes of 150, the 9 hours asleep do not count");
    }

    // AC-7: one note before 150 awake minutes in total, the run still goes on.
    [Fact]
    public async Task RunAsync_NineHourJump_NotEndedBefore150AwakeMinutes()
    {
        // Arrange
        ModelRunRequest request = await StartAsync(150);
        AwakeFor(TimeSpan.FromMinutes(60));
        _clock.Sleep(TimeSpan.FromHours(9));

        // Act: the first note after the wake-up holds the jump and its 30 s (asleep), then 89.5 awake minutes: 149.5 in all
        AwakeFor(TimeSpan.FromMinutes(90));

        // Assert
        request.LimitReached.IsCancellationRequested.Should().BeFalse();
    }

    // AC-7: the run ends timeout once its awake time reaches 150 minutes.
    [Fact]
    public async Task RunAsync_NineHourJump_EndedTimeoutAfter150AwakeMinutes()
    {
        // Arrange
        Task<JobReply> run = RunAsync(150);
        await _started.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);
        AwakeFor(TimeSpan.FromMinutes(60));
        _clock.Sleep(TimeSpan.FromHours(9));

        // Act: the first note after the wake-up is asleep, then 90 awake minutes: 150 in all
        AwakeFor(TimeSpan.FromMinutes(90.5));

        // Assert
        JobReply reply = await run.WaitAsync(Guard, TestContext.Current.CancellationToken);
        new { reply.Status, reply.Reason, First = FirstLine(reply) }
            .Should().BeEquivalentTo(new { Status = ReportStatus.Timeout, Reason = "timeout", First = "timeout after 150 min" });
    }

    // AC-8: a run whose wall-clock age reaches 24 hours ends timeout whatever its awake time, with the 24-hour body line.
    [Fact]
    public async Task RunAsync_WallClockAge24Hours_EndedTimeoutWith24HourLine()
    {
        // Arrange: 600 awake minutes allowed; 1 h awake, 22 h asleep, then awake until the run is 24 h old (about 2 awake hours in all)
        Task<JobReply> run = RunAsync(600);
        await _started.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);
        AwakeFor(TimeSpan.FromMinutes(60));
        _clock.Sleep(TimeSpan.FromHours(22));

        // Act
        AwakeFor(TimeSpan.FromMinutes(60));

        // Assert
        JobReply reply = await run.WaitAsync(Guard, TestContext.Current.CancellationToken);
        new { reply.Status, reply.Reason, First = FirstLine(reply) }
            .Should().BeEquivalentTo(new { Status = ReportStatus.Timeout, Reason = "timeout", First = "timeout after 24 h (asleep 22h00)" });
    }

    // AC-8: one note before the 24 hours, the run goes on.
    [Fact]
    public async Task RunAsync_WallClockAgeUnder24Hours_NotEnded()
    {
        // Arrange
        ModelRunRequest request = await StartAsync(600);
        AwakeFor(TimeSpan.FromMinutes(60));
        _clock.Sleep(TimeSpan.FromHours(22));

        // Act
        AwakeFor(TimeSpan.FromMinutes(59.5));

        // Assert
        request.LimitReached.IsCancellationRequested.Should().BeFalse();
    }

    // AC-9: the report of a run that slept carries the asleep line; duration stays wall-clock.
    [Fact]
    public async Task RunAsync_RunThatSlept_BodyCarriesAsleepLineDurationWallClock()
    {
        // Arrange
        Task<JobReply> run = RunAsync(150);
        await _started.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);
        AwakeFor(TimeSpan.FromMinutes(10));
        _clock.Sleep(TimeSpan.FromHours(9).Add(TimeSpan.FromMinutes(7)));
        AwakeFor(TimeSpan.FromMinutes(5));

        // Act
        _finish.SetResult();

        // Assert
        JobReply reply = await run.WaitAsync(Guard, TestContext.Current.CancellationToken);
        new { reply.Status, Line = Lines(reply).Contains("asleep 9h07 during the run"), Hours = (int)(reply.Finished - reply.Started).TotalHours }
            .Should().BeEquivalentTo(new { Status = ReportStatus.Done, Line = true, Hours = 9 });
    }

    // AC-9: a sleep in the last interval, with no note between it and the run's end, is still counted (the end takes a last note).
    [Fact]
    public async Task RunAsync_SleepJustBeforeTheEnd_BodyCarriesAsleepLine()
    {
        // Arrange
        Task<JobReply> run = RunAsync(150);
        await _started.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);
        _clock.Sleep(TimeSpan.FromHours(9));

        // Act
        _finish.SetResult();

        // Assert
        JobReply reply = await run.WaitAsync(Guard, TestContext.Current.CancellationToken);
        Lines(reply).Should().Contain("asleep 9h00 during the run");
    }

    // AC-9: a run that did not sleep carries no asleep line.
    [Fact]
    public async Task RunAsync_RunThatDidNotSleep_NoAsleepLine()
    {
        // Arrange
        Task<JobReply> run = RunAsync(150);
        await _started.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);
        AwakeFor(TimeSpan.FromMinutes(40));

        // Act
        _finish.SetResult();

        // Assert
        JobReply reply = await run.WaitAsync(Guard, TestContext.Current.CancellationToken);
        Encoding.UTF8.GetString(reply.Body.Span).Should().NotContain("asleep");
    }

    // AC-9: a run ended at its awake limit after a sleep says both.
    [Fact]
    public async Task RunAsync_TimeoutAfterSleep_BodyCarriesAsleepLine()
    {
        // Arrange
        Task<JobReply> run = RunAsync(30);
        await _started.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);
        _clock.Sleep(TimeSpan.FromHours(2).Add(TimeSpan.FromMinutes(30)));

        // Act
        AwakeFor(TimeSpan.FromMinutes(31));

        // Assert
        JobReply reply = await run.WaitAsync(Guard, TestContext.Current.CancellationToken);
        new { reply.Status, Line = Lines(reply).Contains("asleep 2h30 during the run") }
            .Should().BeEquivalentTo(new { Status = ReportStatus.Timeout, Line = true });
    }

    // The awake clock's rule: a gap of up to 5 minutes between two notes is awake time, a longer one is asleep.
    [Theory]
    [InlineData(300, 300, 0)]
    [InlineData(301, 0, 301)]
    public void Note_GapBetweenTwoNotes_AwakeUpToFiveMinutesElseAsleep(int gapSeconds, int awakeSeconds, int asleepSeconds)
    {
        // Arrange
        using var awake = new AwakeClock(_clock, TimeSpan.FromHours(1), _clock.GetUtcNow());
        _clock.Sleep(TimeSpan.FromSeconds(gapSeconds));

        // Act
        awake.Note();

        // Assert
        new { awake.Awake, awake.Asleep }.Should().BeEquivalentTo(new { Awake = TimeSpan.FromSeconds(awakeSeconds), Asleep = TimeSpan.FromSeconds(asleepSeconds) });
    }

    // Edge case: the clock set back by hours counts as zero time, never as negative awake time.
    [Fact]
    public void Note_ClockSetBack_CountsZero()
    {
        // Arrange
        using var awake = new AwakeClock(_clock, TimeSpan.FromHours(1), _clock.GetUtcNow());
        _clock.Sleep(TimeSpan.FromMinutes(2));
        awake.Note();
        _clock.Sleep(TimeSpan.FromHours(-3));

        // Act
        awake.Note();

        // Assert
        new { awake.Awake, awake.Asleep }.Should().BeEquivalentTo(new { Awake = TimeSpan.FromMinutes(2), Asleep = TimeSpan.Zero });
    }

    // The job runner hands the limit to the model as a signal; the process's own limit is only a backstop past the 24 hours.
    [Fact]
    public async Task RunAsync_AnyJob_RequestTimeoutIsTheBackstopPast24Hours()
    {
        // Arrange
        Task<JobReply> run = RunAsync(10);
        ModelRunRequest request = await _started.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);

        // Act
        _finish.SetResult();
        await run.WaitAsync(Guard, TestContext.Current.CancellationToken);

        // Assert
        request.Timeout.Should().BeGreaterThan(TimeSpan.FromHours(24));
    }

    private static string FirstLine(JobReply reply) => Lines(reply)[0];

    private static string[] Lines(JobReply reply) => Encoding.UTF8.GetString(reply.Body.Span).Split('\n');

    private async Task<ModelRunRequest> StartAsync(int minutes)
    {
        _ = RunAsync(minutes);
        return await _started.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);
    }

    private async Task<JobReply> RunAsync(int minutes)
    {
        var job = await JobRunnerHarness.JobAsync(new JobFields(_project.Root, TimeoutMinutes: minutes));
        return await _h.Build().RunAsync(job, CancellationToken.None);
    }

    // The machine awake: the clock moves in 30-second steps, so every note sees an awake gap.
    private void AwakeFor(TimeSpan span)
    {
        for (var left = span; left > TimeSpan.Zero; left -= TimeSpan.FromSeconds(30))
        {
            _h.Clock.Advance(left < TimeSpan.FromSeconds(30) ? left : TimeSpan.FromSeconds(30));
        }
    }

    // The model: runs until the test finishes it (done) or the run's limit is reached (timeout with its partial text, as the CLI runner answers).
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

    /// <summary>A wall clock that jumps while the timers stand still: <see cref="Sleep"/> moves the time only, as a suspended machine's clock does.</summary>
    private sealed class SleepingClock(FakeTimeProvider awake) : TimeProvider
    {
        private TimeSpan _slept;

        public void Sleep(TimeSpan span) => _slept += span;

        public override DateTimeOffset GetUtcNow() => awake.GetUtcNow() + _slept;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            awake.CreateTimer(callback, state, dueTime, period);

        public override long GetTimestamp() => awake.GetTimestamp();

        public override long TimestampFrequency => awake.TimestampFrequency;
    }
}
