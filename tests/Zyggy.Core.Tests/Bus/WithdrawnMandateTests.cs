using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;

namespace Zyggy.Core.Tests.Bus;

/// <summary>
/// Spec 61 slice 4, AC-20 (Q4 (a)): a withdrawn job keeps its place in the mandate's <c>max_jobs</c> count (its report is not
/// <c>rejected</c>), and a <c>failed</c> / <c>withdrawn</c> report never counts toward the pause, also when the model had started.
/// </summary>
public sealed class WithdrawnMandateTests
{
    private static readonly EnvelopeId MandateId = EnvelopeId.Parse("01M7F1M0FFFFFFFFFFFFFFFFFF");

    private static readonly DateTimeOffset T0 = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);

    // AC-20: three withdrawn runs in a row (the model had started) do not pause the mandate.
    [Fact]
    public void Paused_ThreeWithdrawnReportsOfStartedRuns_NotPaused()
    {
        // Arrange
        MandateLedger ledger = Ledger(Withdrawn(1), Withdrawn(2), Withdrawn(3));

        // Act
        bool paused = ledger.Paused;

        // Assert
        paused.Should().BeFalse();
    }

    // AC-20: a withdrawn report between failures is skipped, not a break: three other failures still pause.
    [Fact]
    public void Paused_ThreeFailuresAroundAWithdrawnReport_Paused()
    {
        // Arrange
        MandateLedger ledger = Ledger(Failed(1), Failed(2), Withdrawn(3), Failed(4));

        // Act
        bool paused = ledger.Paused;

        // Assert
        paused.Should().BeTrue();
    }

    // AC-20: two failures and a newest withdrawn report are two counted reports, not three.
    [Fact]
    public void Paused_TwoFailuresThenAWithdrawnReport_NotPaused()
    {
        // Arrange
        MandateLedger ledger = Ledger(Failed(1), Failed(2), Withdrawn(3));

        // Act
        bool paused = ledger.Paused;

        // Assert
        paused.Should().BeFalse();
    }

    // AC-20: a withdrawn job counts in max_jobs, a rejected one does not.
    [Fact]
    public void Counted_WithdrawnJobCountsRejectedDoesNot()
    {
        // Arrange
        MandateLedger ledger = Ledger(Withdrawn(1), Report(2, ReportStatus.Rejected, "branch_refused", modelStarted: false));

        // Act
        IReadOnlyList<MandateJobEntry> counted = ledger.Counted;

        // Assert
        counted.Select(j => j.Id).Should().Equal(JobId(1));
    }

    private static MandateLedger Ledger(params MandateReportEntry[] reports) => new(MandateId, null, true, false, [], reports, null);

    private static MandateReportEntry Withdrawn(int n) => Report(n, ReportStatus.Failed, "withdrawn", modelStarted: true);

    private static MandateReportEntry Failed(int n) => Report(n, ReportStatus.Failed, "claude_error", modelStarted: true);

    private static MandateReportEntry Report(int n, ReportStatus status, string reason, bool modelStarted) =>
        new(EnvelopeId.Parse("01M87C" + new string('0', 18) + "R" + n), JobId(n), T0.AddMinutes(n), status, reason, "slice 4", UsageLimit: false, modelStarted);

    private static EnvelopeId JobId(int n) => EnvelopeId.Parse("01M87C" + new string('0', 18) + "J" + n);
}
