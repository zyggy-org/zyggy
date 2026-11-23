using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Bus;

/// <summary>
/// Spec 50 slice 1 (AC-6): a report writer copies the job's <c>job_type</c>, so a report alone tells which type of job it answered;
/// a job without one gets a report without one. The sweep's re-queue keeps <c>model</c> and <c>job_type</c> unchanged.
/// </summary>
public sealed class JobTypeReportTests : IDisposable
{
    private readonly SweepClaimsHarness _sweep = new();

    public void Dispose() => _sweep.Dispose();

    [Theory]
    [InlineData("implementation")]
    [InlineData(null)]
    public async Task RunAsync_ExhaustedClaim_ReportCarriesTheJobsJobType(string? jobType)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await _sweep.DropClaimAsync(SweepClaimsHarness.HomeLaptop, ct, attempt: 3, model: "claude-opus-5-5", jobType: jobType);
        BusSweep sweep = await _sweep.SweepAsync(ct);

        // Act
        await sweep.RunAsync(ct);

        // Assert
        Model report = await _sweep.ReadVerifiedAsync(ReportPath(), ct);
        report.Report!.JobType.Should().Be(jobType);
    }

    [Fact]
    public async Task RunAsync_RefusedClaim_RejectionReportCarriesTheJobsJobType()
    {
        // Arrange: a claim in home-laptop's folder addressed to central is refused (wrong_recipient) by the sweep
        var ct = TestContext.Current.CancellationToken;
        await _sweep.DropClaimAsync(SweepClaimsHarness.HomeLaptop, ct, to: SweepClaimsHarness.Central, model: "claude-opus-5-5", jobType: "spec");
        BusSweep sweep = await _sweep.SweepAsync(ct);

        // Act
        await sweep.RunAsync(ct);

        // Assert
        Model report = await _sweep.ReadVerifiedAsync(ReportPath(), ct);
        (report.Report!.Status, report.Report.JobType).Should().Be((ReportStatus.Rejected, "spec"));
    }

    [Fact]
    public async Task RunAsync_StaleClaimRequeued_ModelAndJobTypeKept()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (_, Model job, _) = await _sweep.DropClaimAsync(SweepClaimsHarness.HomeLaptop, ct, attempt: 1, model: "claude-opus-5-5", jobType: "implementation");
        BusSweep sweep = await _sweep.SweepAsync(ct);

        // Act
        await sweep.RunAsync(ct);

        // Assert
        string requeued = _sweep.Checkout.FilesUnder(BusPaths.Jobs(SweepClaimsHarness.Acme, SweepClaimsHarness.HomeLaptop)).Should().ContainSingle().Subject;
        JobFields fields = (await _sweep.ReadVerifiedAsync(requeued, ct)).Job!;
        new { fields.Model, fields.JobType, fields.Attempt }.Should().BeEquivalentTo(new { Model = "claude-opus-5-5", JobType = "implementation", Attempt = job.Job!.Attempt + 1 });
    }

    private string ReportPath() =>
        _sweep.Checkout.FilesUnder(BusPaths.Reports(SweepClaimsHarness.Acme, SweepClaimsHarness.Central)).Should().ContainSingle().Subject;
}
