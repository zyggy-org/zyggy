using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Supervision;
using Zyggy.Core.Tenancy;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Supervision;

// Spec 18 slice 10: the pure parts of Central's wake — the text of the decisions the node writes itself (AC-24) and the hourly cap
// on supervisor runs (AC-21).
public sealed class SupervisorWakeTests
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly DateTimeOffset Granted = new(2026, 11, 10, 8, 0, 0, TimeSpan.Zero);

    private static readonly EnvelopeId MandateId = EnvelopeId.New(Granted);

    private int _sequence;

    // AC-24
    [Fact]
    public void Stop_DecisionsUnderTheMandate_ListsAcceptedStepsCountsPerKindAndWhatWaits()
    {
        // Arrange
        EnvelopeId verify1 = Id();
        EnvelopeId verify2 = Id();
        EnvelopeId question = Id();
        EnvelopeId report = Id();
        DecisionRecord[] decisions =
        [
            Decision(verify1, DecisionKind.Accept, "slice 1", [verify1.Value]),
            Decision(report, DecisionKind.Correct, "slice 2"),
            Decision(verify2, DecisionKind.Accept, "slice 2", [verify2.Value, "0123abc"]),
            Decision(question, DecisionKind.Escalate),
        ];

        // Act
        string text = SupervisorSummary.Stop(MandateState.Exhausted, Ledger(maxJobs: 3, reports: 3), decisions);

        // Assert
        text.Should().Be(
            "mandate exhausted: 3 of 3 jobs used\n"
            + $"accepted: slice 1 ({verify1.Value}); slice 2 ({verify2.Value}, 0123abc)\n"
            + "decisions: accept 2, correct 1, escalate 1\n"
            + $"waiting for the owner: {question.Value}");
    }

    // AC-24
    [Fact]
    public void Stop_NothingDecided_SaysSo()
    {
        // Act
        string text = SupervisorSummary.Stop(MandateState.Expired, Ledger(maxJobs: 5, reports: 0), []);

        // Assert
        text.Should().Be("mandate expired: 0 of 5 jobs used\naccepted: none\ndecisions: none\nwaiting for the owner: nothing");
    }

    // AC-24: an event whose newest decision is no longer the escalate does not wait.
    [Fact]
    public void Stop_EscalateFollowedByAnAnswerOnTheSameEvent_NothingWaits()
    {
        // Arrange
        EnvelopeId question = Id();
        DecisionRecord[] decisions = [Decision(question, DecisionKind.Escalate), Decision(question, DecisionKind.Answer)];

        // Act
        string text = SupervisorSummary.Stop(MandateState.Revoked, Ledger(maxJobs: 5, reports: 1), decisions);

        // Assert
        text.Should().EndWith("decisions: answer 1, escalate 1\nwaiting for the owner: nothing");
    }

    // AC-24: decisions of another mandate are not part of this mandate's summary.
    [Fact]
    public void Stop_DecisionOfAnotherMandate_LeftOut()
    {
        // Arrange
        DecisionRecord other = Decision(Id(), DecisionKind.Accept, "slice 9") with { Fields = new DecisionFields(DecisionKind.Accept, DecisionBasis.Verified, "nothing to undo", Id()) };

        // Act
        string text = SupervisorSummary.Stop(MandateState.Expired, Ledger(maxJobs: 5, reports: 0), [other]);

        // Assert
        text.Should().Contain("accepted: none\ndecisions: none");
    }

    // AC-24: the summary fits a decision body.
    [Fact]
    public void Stop_MoreAcceptedStepsThanFit_CutWithAndNMore()
    {
        // Arrange
        DecisionRecord[] decisions = [.. Enumerable.Range(1, 60).Select(n =>
        {
            EnvelopeId verify = Id();
            return Decision(verify, DecisionKind.Accept, "slice " + n, [verify.Value]);
        })];

        // Act
        string text = SupervisorSummary.Stop(MandateState.Exhausted, Ledger(maxJobs: 60, reports: 60), decisions);

        // Assert
        new { Fits = text.Length <= 1200, Cut = text.Split('\n')[1].EndsWith(" more", StringComparison.Ordinal), Counted = text.Contains("decisions: accept 60\n", StringComparison.Ordinal) }
            .Should().BeEquivalentTo(new { Fits = true, Cut = true, Counted = true }, text);
    }

    // AC-24
    [Fact]
    public void Paused_NamesTheThreeReportsAndTheResumeCommand()
    {
        // Arrange
        MandateLedger ledger = Ledger(maxJobs: 10, reports: 3, status: ReportStatus.Failed);

        // Act
        string text = SupervisorSummary.Paused(ledger);

        // Assert
        text.Should().Be(
            "mandate paused: its three newest reports are not done ("
            + string.Join(", ", ledger.Reports.Select(r => r.Id.Value + " failed claude_error"))
            + "); resume with: zyggy mandate resume " + MandateId.Value);
    }

    // AC-21
    [Fact]
    public void Budget_CapReached_NextStartRefused()
    {
        // Arrange
        var budget = new SupervisorRunBudget(2);
        budget.TryStart(Granted);
        budget.TryStart(Granted.AddMinutes(1));

        // Act
        bool third = budget.TryStart(Granted.AddMinutes(59));

        // Assert
        third.Should().BeFalse();
    }

    // AC-21: a run that started 60 minutes ago no longer counts.
    [Theory]
    [InlineData(59, false)]
    [InlineData(60, true)]
    public void Budget_FirstRunLeavesTheHour_NextStartPassesAtSixtyMinutes(int minutes, bool allowed)
    {
        // Arrange
        var budget = new SupervisorRunBudget(1);
        budget.TryStart(Granted);

        // Act
        bool next = budget.TryStart(Granted.AddMinutes(minutes));

        // Assert
        next.Should().Be(allowed);
    }

    // AC-21: a refused start is not counted against the cap.
    [Fact]
    public void Budget_RefusedStart_NotCounted()
    {
        // Arrange
        var budget = new SupervisorRunBudget(1);
        budget.TryStart(Granted);
        budget.TryStart(Granted.AddMinutes(30));

        // Act
        bool next = budget.TryStart(Granted.AddMinutes(60));

        // Assert
        next.Should().BeTrue();
    }

    private EnvelopeId Id() => EnvelopeId.New(Granted.AddMinutes(++_sequence));

    private DecisionRecord Decision(EnvelopeId inReplyTo, DecisionKind kind, string? step = null, IReadOnlyList<string>? evidence = null)
    {
        EnvelopeId id = Id();
        return new DecisionRecord(
            id,
            HomeLaptop,
            id.Timestamp,
            inReplyTo,
            new DecisionFields(kind, DecisionBasis.Verified, "nothing to undo", MandateId, "shop", step, evidence),
            "text",
            false);
    }

    private MandateLedger Ledger(int maxJobs, int reports, ReportStatus status = ReportStatus.Done)
    {
        var fields = new MandateFields("shop", "Build the shop", ["Read"], maxJobs, Granted.AddDays(3));
        Model mandate = Model.CreateMandate(new EnvelopeHeader(Acme, MandateId, MachineName.Central, HomeLaptop, Granted), fields);
        MandateReportEntry[] entries = [.. Enumerable.Range(0, reports).Select(_ =>
        {
            EnvelopeId job = Id();
            EnvelopeId report = Id();
            return new MandateReportEntry(report, job, report.Timestamp, status, status == ReportStatus.Done ? null : "claude_error", "slice 1", false);
        })];
        return new MandateLedger(MandateId, mandate, true, false, [], entries, null);
    }
}
