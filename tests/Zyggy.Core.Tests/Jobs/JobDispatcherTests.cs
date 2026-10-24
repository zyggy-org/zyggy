using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Zyggy.Core.Configuration;
using Zyggy.Core.Envelope;
using Zyggy.Core.Jobs;
using Zyggy.Core.Policy;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Jobs;

/// <summary>Spec 07 AC-4 and AC-5: the §6 admission rules in order, the lease, and one log line per deferral episode.</summary>
public sealed class JobDispatcherTests : IDisposable
{
    private static readonly MachineName Central = MachineName.Parse("central");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    /// <summary>Monday 2026-10-19 10:00Z = 12:00 CEST, inside the Brussels window.</summary>
    private static readonly DateTimeOffset Inside = new(2026, 10, 19, 10, 0, 0, TimeSpan.Zero);

    /// <summary>Monday 2026-10-19 20:00Z = 22:00 CEST, outside the window.</summary>
    private static readonly DateTimeOffset Outside = new(2026, 10, 19, 20, 0, 0, TimeSpan.Zero);

    private readonly IPolicySource _policy = Substitute.For<IPolicySource>();

    private readonly FakeTimeProvider _clock = new(Inside);

    private readonly RecordingLogger<JobDispatcher> _log = new();

    private readonly ScratchProject _project = ScratchProject.Create();

    private readonly ScratchProject _other = ScratchProject.Create();

    private NodePolicy _nodePolicy = NodePolicy.Default with
    {
        MaxConcurrentJobs = 2,
        WorkHours = WorkHours.FromSettings(new WorkHoursSettings("Europe/Brussels", ["Mon", "Tue", "Wed", "Thu", "Fri"], "08:00", "19:00")),
    };

    public JobDispatcherTests() => _policy.GetAsync(Arg.Any<CancellationToken>()).Returns(_ => _nodePolicy);

    public void Dispose()
    {
        _project.Dispose();
        _other.Dispose();
    }

    [Theory]
    [InlineData("normal-inside", true)]
    [InlineData("normal-outside", false)]
    [InlineData("high-outside", true)]
    public async Task AdmitAsync_WorkHoursRule_PriorityHighSkipsIt(string scenario, bool admitted)
    {
        // Arrange
        _clock.SetUtcNow(scenario.EndsWith("inside", StringComparison.Ordinal) ? Inside : Outside);
        Model job = await JobAsync(_project.Root, scenario.StartsWith("high", StringComparison.Ordinal) ? EnvelopePriority.High : EnvelopePriority.Normal);

        // Act
        AdmissionDecision decision = await Dispatcher().AdmitAsync(job, TestContext.Current.CancellationToken);

        // Assert
        if (admitted)
        {
            decision.Should().BeOfType<Admitted>();
        }
        else
        {
            decision.Should().Be(new Deferred(DeferralReason.OutsideWorkHours));
        }
    }

    [Fact]
    public async Task AdmitAsync_TwoLiveLeases_DeferredNoSlot()
    {
        // Arrange
        JobDispatcher dispatcher = Dispatcher();
        (await dispatcher.AdmitAsync(await JobAsync("calizr"), TestContext.Current.CancellationToken)).Should().BeOfType<Admitted>();
        (await dispatcher.AdmitAsync(await JobAsync("weather"), TestContext.Current.CancellationToken)).Should().BeOfType<Admitted>();

        // Act
        AdmissionDecision third = await dispatcher.AdmitAsync(await JobAsync(_project.Root), TestContext.Current.CancellationToken);

        // Assert
        third.Should().Be(new Deferred(DeferralReason.NoSlot));
        dispatcher.JobsRunning.Should().Be(2);
    }

    [Fact]
    public async Task AdmitAsync_ProjectAlreadyLeased_DeferredProjectBusy()
    {
        // Arrange
        JobDispatcher dispatcher = Dispatcher();
        (await dispatcher.AdmitAsync(await JobAsync(_project.Root), TestContext.Current.CancellationToken)).Should().BeOfType<Admitted>();

        // Act: the same project written with a trailing separator still names the same directory
        AdmissionDecision second = await dispatcher.AdmitAsync(await JobAsync(_project.Root + Path.DirectorySeparatorChar), TestContext.Current.CancellationToken);

        // Assert
        second.Should().Be(new Deferred(DeferralReason.ProjectBusy));
    }

    [Fact]
    public async Task AdmitAsync_ProjectLockHeldElsewhere_DeferredProjectBusy()
    {
        // Arrange: another process (here: another handle) holds the project's lock
        using ProjectLock? held = ProjectLock.TryAcquire(_project.Root, EnvelopeId.New(Inside), 4242, Inside);
        held.Should().NotBeNull();

        // Act
        AdmissionDecision decision = await Dispatcher().AdmitAsync(await JobAsync(_project.Root), TestContext.Current.CancellationToken);

        // Assert
        decision.Should().Be(new Deferred(DeferralReason.ProjectBusy));
    }

    [Fact]
    public async Task AdmitAsync_HighOutsideHoursNoSlot_DeferredNoSlot()
    {
        // Arrange: `high` skips only the work-hours rule
        _clock.SetUtcNow(Outside);
        JobDispatcher dispatcher = Dispatcher();
        (await dispatcher.AdmitAsync(await JobAsync("a", EnvelopePriority.High), TestContext.Current.CancellationToken)).Should().BeOfType<Admitted>();
        (await dispatcher.AdmitAsync(await JobAsync("b", EnvelopePriority.High), TestContext.Current.CancellationToken)).Should().BeOfType<Admitted>();

        // Act
        AdmissionDecision third = await dispatcher.AdmitAsync(await JobAsync("c", EnvelopePriority.High), TestContext.Current.CancellationToken);

        // Assert
        third.Should().Be(new Deferred(DeferralReason.NoSlot));
    }

    [Fact]
    public async Task AdmitAsync_RegistryStyleProject_AdmittedWithoutLockCheck()
    {
        // Act: a registry name is not a path; the runner answers unknown_project (06), the dispatcher does not touch a lock file
        AdmissionDecision decision = await Dispatcher().AdmitAsync(await JobAsync("calizr"), TestContext.Current.CancellationToken);

        // Assert
        decision.Should().BeOfType<Admitted>();
        File.Exists(_project.LockPath).Should().BeFalse();
    }

    [Fact]
    public async Task AdmitAsync_WorkHoursNull_AlwaysInside()
    {
        // Arrange
        _nodePolicy = _nodePolicy with { WorkHours = null };
        _clock.SetUtcNow(Outside);

        // Act
        AdmissionDecision decision = await Dispatcher().AdmitAsync(await JobAsync(_project.Root), TestContext.Current.CancellationToken);

        // Assert
        decision.Should().BeOfType<Admitted>();
    }

    [Fact]
    public async Task Lease_Dispose_FreesSlotAndProject()
    {
        // Arrange
        JobDispatcher dispatcher = Dispatcher();
        var first = (Admitted)await dispatcher.AdmitAsync(await JobAsync(_project.Root), TestContext.Current.CancellationToken);
        dispatcher.JobsRunning.Should().Be(1);

        // Act
        first.Lease.Dispose();
        first.Lease.Dispose();
        AdmissionDecision again = await dispatcher.AdmitAsync(await JobAsync(_project.Root), TestContext.Current.CancellationToken);

        // Assert
        again.Should().BeOfType<Admitted>();
        dispatcher.JobsRunning.Should().Be(1);
    }

    [Fact]
    public async Task AdmitAsync_ThreeDeferralsThenAdmitted_TwoLogLinesOnly()
    {
        // Arrange
        JobDispatcher dispatcher = Dispatcher();
        Model job = await JobAsync(_project.Root);
        _clock.SetUtcNow(Outside);

        // Act
        for (var i = 0; i < 3; i++)
        {
            (await dispatcher.AdmitAsync(job, TestContext.Current.CancellationToken)).Should().Be(new Deferred(DeferralReason.OutsideWorkHours));
        }

        _clock.SetUtcNow(Inside.AddDays(1)); // Tuesday 12:00 CEST: the clock only moves forward
        (await dispatcher.AdmitAsync(job, TestContext.Current.CancellationToken)).Should().BeOfType<Admitted>();

        // Assert: §11 once per state change
        string id = job.Header.Id.Value;
        _log.Messages.Should().Equal($"job {id} deferred: OutsideWorkHours", $"job {id} admitted after deferral");
        _log.Entries.Should().AllSatisfy(e =>
        {
            var scope = e.Scopes.OfType<IReadOnlyDictionary<string, object>>().Should().ContainSingle().Subject;
            scope.Should().Contain("tenant", "acme").And.Contain("machine", "home-laptop").And.Contain("phase", "claim").And.Contain("jobId", id);
        });
    }

    [Fact]
    public async Task AdmitAsync_MaxConcurrentJobsFive_OneAdvisoryWarningOnce()
    {
        // Arrange
        _nodePolicy = _nodePolicy with { MaxConcurrentJobs = 5 };
        JobDispatcher dispatcher = Dispatcher();

        // Act
        await dispatcher.AdmitAsync(await JobAsync("a"), TestContext.Current.CancellationToken);
        await dispatcher.AdmitAsync(await JobAsync("b"), TestContext.Current.CancellationToken);

        // Assert
        _log.AtLevel(LogLevel.Warning).Select(e => e.Message).Should().ContainSingle()
            .Which.Should().Be("maxConcurrentJobs 5 > 4: more than four parallel claude runs is unlikely to be intended");
    }

    [Fact]
    public async Task AdmitAsync_NotAJob_ThrowsArgumentException()
    {
        // Arrange
        Model job = await JobAsync(_project.Root);
        var store = await TestKeys.StoreWithBothAsync(TestContext.Current.CancellationToken);
        var header = new EnvelopeHeader(TestKeys.Acme1.Tenant, EnvelopeId.New(Inside), HomeLaptop, Central, Inside, InReplyTo: job.Header.Id);
        Model report = Model.CreateReport(header, new ReportFields(ReportStatus.Done), "ok\n"u8.ToArray());

        // Act
        Func<Task> act = () => Dispatcher().AdmitAsync(report, TestContext.Current.CancellationToken);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task AdmitAsync_Concurrent_NeverExceedsMaxConcurrentJobs()
    {
        // Arrange
        JobDispatcher dispatcher = Dispatcher();
        Model[] jobs = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => JobAsync("project-" + i)));

        // Act
        AdmissionDecision[] decisions = await Task.WhenAll(jobs.Select(j => Task.Run(() => dispatcher.AdmitAsync(j, TestContext.Current.CancellationToken))));

        // Assert
        decisions.OfType<Admitted>().Should().HaveCount(2);
        decisions.OfType<Deferred>().Should().HaveCount(6).And.AllSatisfy(d => d.Reason.Should().Be(DeferralReason.NoSlot));
        dispatcher.JobsRunning.Should().Be(2);
    }

    private JobDispatcher Dispatcher() => new(_policy, _clock, _log);

    private static async Task<Model> JobAsync(string project, EnvelopePriority priority = EnvelopePriority.Normal)
    {
        var store = await TestKeys.StoreWithBothAsync(CancellationToken.None);
        return await TestEnvelopes.SignedJobAsync(Central, HomeLaptop, store, TestKeys.Acme1, "Investigate 502.\n", CancellationToken.None, EnvelopeId.New(Inside), new JobFields(project), priority);
    }
}
