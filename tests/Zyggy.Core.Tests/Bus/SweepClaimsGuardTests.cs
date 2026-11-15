using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Bus;

/// <summary>
/// Spec 17 AC-6: the write-rule guard lets Central make exactly the sweep's moves (the allowed side is <see cref="SweepClaimsTests"/>)
/// and refuses them for a machine that is not Central, for another tenant's subtree, across machines and outside <c>jobs/claimed/</c>.
/// </summary>
public sealed class SweepClaimsGuardTests : IDisposable
{
    private readonly SweepClaimsHarness _h = new();

    public void Dispose() => _h.Dispose();

    public static TheoryData<string, string> RefusedSources => new()
    {
        { "tenants/other-org/nodes/home-laptop/jobs/claimed", "*another tenant*" },
        { "tenants/acme/nodes/work-laptop/jobs/claimed", "*another machine*" },
        { "tenants/acme/nodes/home-laptop/jobs", "*not in the Claimed folder*" },
        { "tenants/acme/nodes/home-laptop/reports", "*not in the Claimed folder*" },
    };

    [Fact]
    public async Task RequeueAsync_MachineIsNotCentral_Refused()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (SweepClaim claim, Model job) = await ClaimAsync(BusPaths.Claimed(SweepClaimsHarness.Acme, SweepClaimsHarness.HomeLaptop), ct);
        BusRepository bus = await _h.RepositoryAsync(ct, me: SweepClaimsHarness.HomeLaptop);

        // Act
        Func<Task> act = () => bus.RequeueAsync(claim, job, ct);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*only central sweeps*");
    }

    [Fact]
    public async Task ExhaustAsync_MachineIsNotCentral_Refused()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (SweepClaim claim, Model job) = await ClaimAsync(BusPaths.Claimed(SweepClaimsHarness.Acme, SweepClaimsHarness.HomeLaptop), ct);
        Model report = await TestEnvelopes.SignedReportAsync(
            SweepClaimsHarness.HomeLaptop, SweepClaimsHarness.WorkLaptop, job.Header.Id, await _h.StoreAsync(ct), TestKeys.Acme2, "x\n", ct, ReportStatus.Failed, "attempts_exhausted");
        BusRepository bus = await _h.RepositoryAsync(ct, me: SweepClaimsHarness.HomeLaptop);

        // Act
        Func<Task> act = () => bus.ExhaustAsync(claim, report, ct);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*only central sweeps*");
    }

    [Fact]
    public async Task RejectStaleAsync_MachineIsNotCentral_Refused()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (SweepClaim claim, _) = await ClaimAsync(BusPaths.Claimed(SweepClaimsHarness.Acme, SweepClaimsHarness.HomeLaptop), ct);
        BusRepository bus = await _h.RepositoryAsync(ct, me: SweepClaimsHarness.HomeLaptop);

        // Act
        Func<Task> act = () => bus.RejectStaleAsync(claim, null, ct);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*only central sweeps*");
    }

    [Theory]
    [MemberData(nameof(RefusedSources))]
    public async Task RequeueAsync_SourceOutsideTheMachinesClaimedFolder_Refused(string folder, string message)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (SweepClaim claim, Model job) = await ClaimAsync(folder, ct);
        BusRepository bus = await _h.RepositoryAsync(ct);

        // Act
        Func<Task> act = () => bus.RequeueAsync(claim, job, ct);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage(message);
        _h.Processes.Calls.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(RefusedSources))]
    public async Task RejectStaleAsync_SourceOutsideTheMachinesClaimedFolder_Refused(string folder, string message)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (SweepClaim claim, _) = await ClaimAsync(folder, ct);
        BusRepository bus = await _h.RepositoryAsync(ct);

        // Act
        Func<Task> act = () => bus.RejectStaleAsync(claim, null, ct);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage(message);
        _h.Processes.Calls.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(RefusedSources))]
    public async Task ExhaustAsync_SourceOutsideTheMachinesClaimedFolder_Refused(string folder, string message)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (SweepClaim claim, Model job) = await ClaimAsync(folder, ct);
        Model report = await ExhaustedReportAsync(SweepClaimsHarness.Central, job.Header.Id, ct);
        BusRepository bus = await _h.RepositoryAsync(ct);

        // Act
        Func<Task> act = () => bus.ExhaustAsync(claim, report, ct);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage(message);
        _h.Processes.Calls.Should().BeEmpty();
    }

    // The report Central adds must be its own signed report on that very claim: another sender or another job is refused.
    [Theory]
    [InlineData("home-laptop", true)]
    [InlineData("central", false)]
    public async Task ExhaustAsync_ReportOfAnotherMachineOrOnAnotherJob_Refused(string reporter, bool onTheClaim)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (SweepClaim claim, Model job) = await ClaimAsync(BusPaths.Claimed(SweepClaimsHarness.Acme, SweepClaimsHarness.HomeLaptop), ct);
        Model report = await ExhaustedReportAsync(MachineName.Parse(reporter), onTheClaim ? job.Header.Id : EnvelopeId.New(SweepClaimsHarness.Now), ct);
        BusRepository bus = await _h.RepositoryAsync(ct);

        // Act
        Func<Task> act = () => bus.ExhaustAsync(claim, report, ct);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not a signed report of this machine*");
        _h.Processes.Calls.Should().BeEmpty();
    }

    private async Task<Model> ExhaustedReportAsync(MachineName from, EnvelopeId inReplyTo, CancellationToken ct) =>
        await TestEnvelopes.SignedReportAsync(
            from, SweepClaimsHarness.WorkLaptop, inReplyTo, await _h.StoreAsync(ct), TestKeys.Acme2, "x\n", ct, ReportStatus.Failed, "attempts_exhausted");

    // A claim said to sit in home-laptop's folder, at a path in <paramref name="folder"/>, with the job re-signed at attempt 2.
    private async Task<(SweepClaim Claim, Model Job)> ClaimAsync(string folder, CancellationToken ct)
    {
        Model job = await TestEnvelopes.SignedJobAsync(
            SweepClaimsHarness.WorkLaptop, SweepClaimsHarness.HomeLaptop, await _h.StoreAsync(ct), TestKeys.Acme2, "probe body\n", ct, fields: new JobFields("probe", Attempt: 2));
        var file = new BusFile(folder + "/" + job.Header.Id.Value + ".md", job.Header.Id);
        return (new SweepClaim(file, MachineName.Parse("home-laptop"), EnvelopeWriter.Serialize(job), SweepClaimsHarness.Now.AddHours(-1)), job);
    }
}
