using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Tests.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Bus;

/// <summary>
/// Spec 18 slice 14 (AC-2, session-job removal): the write-rule guard lets Central remove only an unclaimed <c>deliver: session</c> job
/// past its timeout from a machine's <c>jobs/</c>, with its own <c>no_session</c> report; it refuses another machine than Central, another
/// tenant's subtree, a path outside <c>jobs/</c>, a <c>run</c> job and a job still inside its timeout. The allowed side is
/// <c>SessionTaskSweepTests</c>.
/// </summary>
public sealed class SessionTaskGuardTests : IDisposable
{
    private readonly SweepClaimsHarness _h = new();

    public void Dispose() => _h.Dispose();

    public static TheoryData<string, string> RefusedFolders => new()
    {
        { "tenants/other-org/nodes/home-laptop/jobs", "*another tenant*" },
        { "tenants/acme/nodes/home-laptop/jobs/claimed", "*not in the Jobs folder*" },
        { "tenants/acme/nodes/work-laptop/jobs", "*another machine*" },
    };

    // AC-2
    [Fact]
    public async Task ExpireSessionTaskAsync_MachineIsNotCentral_Refused()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (SweepClaim task, Model report) = await TaskAsync(BusPaths.Jobs(SweepClaimsHarness.Acme, SweepClaimsHarness.HomeLaptop), JobDelivery.Session, TimeSpan.FromHours(2), ct);
        BusRepository bus = await _h.RepositoryAsync(ct, me: SweepClaimsHarness.HomeLaptop);

        // Act
        Func<Task> act = () => bus.ExpireSessionTaskAsync(task, report, ct);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*only central sweeps*");
    }

    // AC-2
    [Theory]
    [MemberData(nameof(RefusedFolders))]
    public async Task ExpireSessionTaskAsync_OutsideTheMachinesJobsFolder_Refused(string folder, string message)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (SweepClaim task, Model report) = await TaskAsync(folder, JobDelivery.Session, TimeSpan.FromHours(2), ct);
        BusRepository bus = await _h.RepositoryAsync(ct);

        // Act
        Func<Task> act = () => bus.ExpireSessionTaskAsync(task, report, ct);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage(message);
        _h.Processes.Calls.Should().BeEmpty();
    }

    // AC-2: a job the node runs itself is never removed this way.
    [Fact]
    public async Task ExpireSessionTaskAsync_RunJob_Refused()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (SweepClaim task, Model report) = await TaskAsync(BusPaths.Jobs(SweepClaimsHarness.Acme, SweepClaimsHarness.HomeLaptop), JobDelivery.Run, TimeSpan.FromHours(2), ct);
        BusRepository bus = await _h.RepositoryAsync(ct);

        // Act
        Func<Task> act = () => bus.ExpireSessionTaskAsync(task, report, ct);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not a session task*");
        _h.Processes.Calls.Should().BeEmpty();
    }

    // AC-2: a session task still inside its 60 minutes stays where it is.
    [Fact]
    public async Task ExpireSessionTaskAsync_InsideItsTimeout_Refused()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (SweepClaim task, Model report) = await TaskAsync(BusPaths.Jobs(SweepClaimsHarness.Acme, SweepClaimsHarness.HomeLaptop), JobDelivery.Session, TimeSpan.FromMinutes(59), ct);
        BusRepository bus = await _h.RepositoryAsync(ct);

        // Act
        Func<Task> act = () => bus.ExpireSessionTaskAsync(task, report, ct);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not past its timeout*");
        _h.Processes.Calls.Should().BeEmpty();
    }

    // A session task of home-laptop submitted <paramref name="age"/> before the harness's now, at a path in <paramref name="folder"/>,
    // and Central's no_session report on it.
    private async Task<(SweepClaim Task, Model Report)> TaskAsync(string folder, JobDelivery deliver, TimeSpan age, CancellationToken ct)
    {
        DateTimeOffset created = SweepClaimsHarness.Now - age;
        var header = new EnvelopeHeader(SweepClaimsHarness.Acme, EnvelopeId.New(created), SweepClaimsHarness.Central, SweepClaimsHarness.HomeLaptop, created);
        Model job = Model.CreateJob(header, new JobFields("shop", TimeoutMinutes: 60, Deliver: deliver), "Build it.\n"u8.ToArray());
        EnvelopeResult signed = await new EnvelopeSigner(await _h.StoreAsync(ct)).SignAsync(job, TestKeys.Acme2, ct);
        var file = new BusFile(folder + "/" + job.Header.Id.Value + ".md", job.Header.Id);
        Model report = await TestEnvelopes.SignedReportAsync(
            SweepClaimsHarness.Central, SweepClaimsHarness.Central, job.Header.Id, await _h.StoreAsync(ct), TestKeys.Acme2, "x\n", ct, ReportStatus.Failed, "no_session");
        return (new SweepClaim(file, SweepClaimsHarness.HomeLaptop, EnvelopeWriter.Serialize(signed.Envelope!), created), report);
    }
}
