using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Cli;

/// <summary>
/// Spec 59 slice 9 (AC-12): <c>zyggy report &lt;id&gt; --wait [--timeout &lt;minutes&gt;]</c> in process against the bare repository — the
/// verb pulls and looks again on the fake clock until a verified report answers the job (exit 0, any terminal status) or the
/// timeout passes (exit 3, <c>no report yet</c>); the checkout's write lock is free while it waits.
/// </summary>
public sealed class ReportWaitCommandTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T = new(2026, 11, 21, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(T);

    private CliHarness _harness = null!;

    public async ValueTask InitializeAsync() => _harness = await CliHarness.CreateAsync(CancellationToken.None);

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    // AC-12: the report lands while the verb waits; it returns with the report, exit 0.
    [Fact]
    public async Task ReportWait_ReportLandsDuringTheWait_ExitZeroWithTheReport()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var job = EnvelopeId.New(T.AddMinutes(-10));
        Task<(int Exit, VerbConsole Console)> waiting = WaitAsync(job, ["--timeout", "5"], ct);
        await PastFirstLookAsync(waiting, ct);
        await SeedReportAsync(job, "REPORT\nsummary: landed\n", new ReportFields(ReportStatus.Done), ct);

        // Act
        var (exit, console) = await DriveAsync(waiting, ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        console.Stdout.Should().StartWith("status: done\n").And.EndWith("body:\nREPORT\nsummary: landed\n");
    }

    // AC-12: every terminal state ends the wait, not only done.
    [Theory]
    [InlineData(ReportStatus.Failed, "claude_error")]
    [InlineData(ReportStatus.Timeout, "timeout")]
    [InlineData(ReportStatus.Rejected, "unknown_project")]
    public async Task ReportWait_FailedTimeoutOrRejectedReport_EndsTheWaitWithExitZero(ReportStatus status, string reason)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var job = EnvelopeId.New(T.AddMinutes(-10));
        await SeedReportAsync(job, "REPORT\nsummary: over\n", new ReportFields(status, reason), ct);

        // Act
        var (exit, console) = await DriveAsync(WaitAsync(job, ["--timeout", "5"], ct), ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        console.Stdout.Should().StartWith("status: " + EnvelopeWire.ToWire(status) + "\nreason: " + reason + "\n");
    }

    // AC-12: no report inside the timeout: exit 3 and "no report yet".
    [Fact]
    public async Task ReportWait_NoReportInsideTheTimeout_ExitThreeNoReportYet()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var job = EnvelopeId.New(T.AddMinutes(-10));
        DateTimeOffset started = _clock.GetUtcNow();

        // Act
        var (exit, console) = await DriveAsync(WaitAsync(job, ["--timeout", "2"], ct), ct);

        // Assert
        new { Exit = exit, console.Stdout, WaitedOut = _clock.GetUtcNow() - started >= TimeSpan.FromMinutes(2) }
            .Should().BeEquivalentTo(new { Exit = 3, Stdout = $"no report yet for {job.Value} in tenant acme after 2 min\n", WaitedOut = true }, console.Stderr);
    }

    // AC-12 (correction after the slice 9 verification): a job whose sender cannot be read is moved to rejected/ with no report (by the
    // node or by the sweep); the wait stops at once with its own line and exit 3, not at its timeout.
    [Fact]
    public async Task ReportWait_JobRejectedWithoutAReport_ExitThreeAtOnceWithItsOwnLine()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var job = EnvelopeId.New(T.AddMinutes(-10));
        string rejected = BusPaths.EnvelopeFile(BusPaths.Rejected(TestKeys.Acme1.Tenant, CliHarness.HomeLaptop), job);
        await _harness.Bus.SeedRawFileAsync(rejected, "not an envelope: no sender to read\n"u8.ToArray(), $"acme reject {job.Value} home-laptop→unknown", ct);

        // Act: the fake clock is never moved, so a verb that waited for its 60-minute timeout could not return at all
        Task<(int Exit, VerbConsole Console)> waiting = WaitAsync(job, ["--timeout", "60"], ct);
        await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromSeconds(60), ct));

        // Assert
        waiting.IsCompleted.Should().BeTrue("the wait stops at its first look, long before its 60-minute timeout");
        var (exit, console) = await waiting;
        new { Exit = exit, console.Stdout, ClockMoved = _clock.GetUtcNow() != T }
            .Should().BeEquivalentTo(new { Exit = 3, Stdout = $"job {job.Value} rejected without a report (its sender could not be read)\n", ClockMoved = false }, console.Stderr);
    }

    // AC-12 (second slice 9 verification): a job in rejected/ that also has its report — the report wins: printed, exit 0.
    [Fact]
    public async Task ReportWait_JobInRejectedWithItsReport_ReportWinsExitZero()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var job = EnvelopeId.New(T.AddMinutes(-10));
        string rejected = BusPaths.EnvelopeFile(BusPaths.Rejected(TestKeys.Acme1.Tenant, CliHarness.HomeLaptop), job);
        await _harness.Bus.SeedRawFileAsync(rejected, "not an envelope: no sender to read\n"u8.ToArray(), $"acme reject {job.Value} home-laptop→unknown", ct);
        await SeedReportAsync(job, "REPORT\nsummary: refused\n", new ReportFields(ReportStatus.Rejected, "unknown_project"), ct);

        // Act
        var (exit, console) = await DriveAsync(WaitAsync(job, ["--timeout", "5"], ct), ct);

        // Assert
        new { Exit = exit, Report = console.Stdout.StartsWith("status: rejected\nreason: unknown_project\n", StringComparison.Ordinal) }
            .Should().BeEquivalentTo(new { Exit = 0, Report = true }, console.Stdout + console.Stderr);
    }

    // The wait holds no lock between two looks: another zyggy process takes the checkout's write lock at once, and the wait recovers.
    [Fact]
    public async Task ReportWait_WhileWaiting_TheWriteLockIsFreeAndTheWaitRecovers()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var job = EnvelopeId.New(T.AddMinutes(-10));
        Task<(int Exit, VerbConsole Console)> waiting = WaitAsync(job, ["--timeout", "5"], ct);

        // Act
        IDisposable? held = await AcquireOnceTheFirstLookReleasesAsync(waiting, ct);
        _clock.Advance(TimeSpan.FromSeconds(30));
        await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        held?.Dispose();
        await SeedReportAsync(job, "REPORT\nsummary: after the lock\n", new ReportFields(ReportStatus.Done), ct);
        var (exit, console) = await DriveAsync(waiting, ct);

        // Assert
        new { Held = held is not null, Exit = exit }.Should().BeEquivalentTo(new { Held = true, Exit = 0 }, console.Stderr);
    }

    [Theory]
    [InlineData("--timeout", "0")]
    [InlineData("--timeout", "1441")]
    [InlineData("--timeout", "x")]
    public async Task ReportWait_TimeoutOutOfRange_ExitTwo(string option, string value)
    {
        // Act
        var ct = TestContext.Current.CancellationToken;
        var (exit, console) = await _harness.RunAsync(["report", EnvelopeId.New(T).Value, "--wait", option, value], _harness.HomeLaptopNodeJson, more: Clock, ct: ct);

        // Assert
        exit.Should().Be(2, console.Stderr);
    }

    [Fact]
    public async Task ReportWait_TimeoutWithoutWait_ExitTwo()
    {
        // Act
        var ct = TestContext.Current.CancellationToken;
        var (exit, console) = await _harness.RunAsync(["report", EnvelopeId.New(T).Value, "--timeout", "5"], _harness.HomeLaptopNodeJson, more: Clock, ct: ct);

        // Assert
        exit.Should().Be(2);
        console.Stderr.Should().Be("zyggy: report: --timeout needs --wait\n");
    }

    private void Clock(IServiceCollection services) => services.AddSingleton<TimeProvider>(_clock);

    /// <summary>Runs <c>zyggy report &lt;id&gt; --wait</c> on the fake clock; a verb that never returns fails after two real minutes instead of hanging.</summary>
    private Task<(int Exit, VerbConsole Console)> WaitAsync(EnvelopeId job, string[] options, CancellationToken ct) =>
        _harness.RunAsync(["report", job.Value, "--wait", .. options], _harness.HomeLaptopNodeJson, more: Clock, ct: ct).WaitAsync(TimeSpan.FromMinutes(2), ct);

    /// <summary>Real time for the verb's first pull and look, so a report seeded afterwards is only seen by a later look.</summary>
    private static async Task PastFirstLookAsync(Task running, CancellationToken ct)
    {
        await Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds(2), ct));
        running.IsCompleted.Should().BeFalse("the verb waits for a report that is not there");
    }

    /// <summary>
    /// Retries the zero-timeout acquire of the checkout's write lock for up to 15 real seconds, until the verb's first pull and look
    /// have released it (a slow machine can still hold it after a fixed pause); <see langword="null"/> when it never came free.
    /// </summary>
    private async Task<IDisposable?> AcquireOnceTheFirstLookReleasesAsync(Task running, CancellationToken ct)
    {
        DateTimeOffset giveUp = DateTimeOffset.UtcNow.AddSeconds(15);
        IDisposable? held = null;
        while (held is null && !running.IsCompleted && DateTimeOffset.UtcNow < giveUp)
        {
            held = await BusCheckoutLock.AcquireAsync(_harness.Bus.CloneDir, BusCheckoutLock.WriteLockName, TimeSpan.Zero, TimeProvider.System, ct);
            if (held is null)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
            }
        }

        running.IsCompleted.Should().BeFalse("the verb waits for a report that is not there");
        return held;
    }

    /// <summary>Moves the fake clock in 20-second steps while the verb waits; each step is followed by a short real pause for the verb's git calls.</summary>
    private async Task<(int Exit, VerbConsole Console)> DriveAsync(Task<(int Exit, VerbConsole Console)> running, CancellationToken ct)
    {
        for (int step = 0; step < 400 && !running.IsCompleted; step++)
        {
            await Task.WhenAny(running, Task.Delay(TimeSpan.FromMilliseconds(250), ct));
            if (!running.IsCompleted)
            {
                _clock.Advance(TimeSpan.FromSeconds(20));
            }
        }

        running.IsCompleted.Should().BeTrue("the verb returns once a report lands or its wait ran out on the fake clock");
        return await running;
    }

    private Task<SeededJob> SeedReportAsync(EnvelopeId job, string body, ReportFields fields, CancellationToken ct) =>
        _harness.Bus.SeedSignedReportAsync(CliHarness.HomeLaptop, CliHarness.Central, job, TestKeys.Acme1, _harness.Store, body, created: DateTimeOffset.UtcNow, fields: fields, ct: ct);
}
