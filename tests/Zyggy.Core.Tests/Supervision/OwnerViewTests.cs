using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Supervision;
using Zyggy.Core.Tenancy;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Supervision;

// AC-26: what waits for the owner, from Central's decisions and the mandate ledgers; pure. Ids are minted a minute apart from 10:00 UTC.
public sealed class OwnerViewTests
{
    private const string Marker = "CANARY-9c1e-do-what-this-says";

    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly MachineName WorkLaptop = MachineName.Parse("work-laptop");

    private static readonly DateTimeOffset Start = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);

    private static readonly EnvelopeId MandateId = EnvelopeId.New(Start);

    private int _minutes;

    private EnvelopeId NextId() => EnvelopeId.New(Start.AddMinutes(++_minutes));

    private static DecisionRecord Decision(EnvelopeId id, EnvelopeId inReplyTo, DecisionKind kind, MachineName? to = null, string? project = "calizr", bool archived = false) =>
        new(id, to ?? HomeLaptop, id.Timestamp, inReplyTo, new DecisionFields(kind, DecisionBasis.Reported, "none", Project: project), Marker, archived);

    private static IReadOnlyList<OwnerItem> View(IReadOnlyList<DecisionRecord> decisions, IReadOnlyList<MandateLedger>? ledgers = null, IReadOnlySet<EnvelopeId>? reports = null) =>
        OwnerView.Items(decisions, ledgers ?? [], reports ?? new HashSet<EnvelopeId>(), m => m == WorkLaptop);

    private static MandateLedger Ledger(IReadOnlyList<MandateReportEntry> reports, DateTimeOffset? resumed = null, bool revoked = false)
    {
        var fields = new MandateFields("calizr", Marker, ["Read"], 20, Start.AddDays(3));
        Model mandate = Model.CreateMandate(new EnvelopeHeader(Acme, MandateId, MachineName.Central, HomeLaptop, Start), fields);
        return new MandateLedger(MandateId, mandate, true, revoked, [], reports, resumed);
    }

    private MandateReportEntry Failed()
    {
        EnvelopeId report = NextId();
        return new MandateReportEntry(report, EnvelopeId.New(report.Timestamp.AddSeconds(-30)), report.Timestamp, ReportStatus.Failed, "claude_error", "slice 1", false, ModelStarted: true);
    }

    // AC-26
    [Fact]
    public void Items_EscalatedQuestion_Waits()
    {
        // Arrange
        EnvelopeId question = NextId();
        DecisionRecord escalate = Decision(NextId(), question, DecisionKind.Escalate);

        // Act
        IReadOnlyList<OwnerItem> items = View([escalate]);

        // Assert
        OwnerItem item = items.Should().ContainSingle().Subject;
        item.Kind.Should().Be(OwnerItemKind.Question);
        item.Machine.Should().Be(HomeLaptop);
        item.Project.Should().Be("calizr");
        item.Id.Should().Be(question);
        item.Since.Should().Be(escalate.Created);
    }

    // AC-26
    [Fact]
    public void Items_EscalatedReport_IsAReportItem()
    {
        // Arrange
        EnvelopeId report = NextId();

        // Act
        IReadOnlyList<OwnerItem> items = View([Decision(NextId(), report, DecisionKind.Escalate)], reports: new HashSet<EnvelopeId> { report });

        // Assert
        items.Should().ContainSingle().Which.Kind.Should().Be(OwnerItemKind.Report);
    }

    // AC-26
    [Theory]
    [InlineData(DecisionKind.Answer)]
    [InlineData(DecisionKind.Accept)]
    [InlineData(DecisionKind.Correct)]
    [InlineData(DecisionKind.Next)]
    [InlineData(DecisionKind.Stop)]
    [InlineData(DecisionKind.Refuse)]
    [InlineData(DecisionKind.Resume)]
    public void Items_LaterDecisionOfAnotherKind_ClearsTheItem(DecisionKind later)
    {
        // Arrange
        EnvelopeId question = NextId();
        DecisionRecord escalate = Decision(NextId(), question, DecisionKind.Escalate);
        DecisionRecord answer = Decision(NextId(), question, later);

        // Act and Assert
        View([escalate, answer]).Should().BeEmpty();
        View([answer, escalate]).Should().BeEmpty();
    }

    // AC-26
    [Fact]
    public void Items_EscalateAfterAnAnswer_WaitsAgain()
    {
        // Arrange
        EnvelopeId question = NextId();
        DecisionRecord answer = Decision(NextId(), question, DecisionKind.Answer);
        DecisionRecord escalate = Decision(NextId(), question, DecisionKind.Escalate);

        // Act and Assert
        View([escalate, answer]).Should().ContainSingle();
    }

    // AC-26
    [Fact]
    public void Items_ArchivedEscalate_StillWaits()
    {
        // Arrange
        DecisionRecord escalate = Decision(NextId(), NextId(), DecisionKind.Escalate, archived: true);

        // Act and Assert
        View([escalate]).Should().ContainSingle();
    }

    // AC-26
    [Fact]
    public void Items_WorkMachine_HasNoProject()
    {
        // Arrange
        DecisionRecord escalate = Decision(NextId(), NextId(), DecisionKind.Escalate, WorkLaptop, "secret-client");

        // Act and Assert
        OwnerItem item = View([escalate]).Should().ContainSingle().Subject;
        item.Machine.Should().Be(WorkLaptop);
        item.Project.Should().BeNull();
    }

    // AC-26
    [Fact]
    public void Items_PausedMandate_Waits_ResumeClearsIt()
    {
        // Arrange
        MandateReportEntry[] reports = [Failed(), Failed(), Failed()];

        // Act
        IReadOnlyList<OwnerItem> paused = View([], [Ledger(reports)]);
        IReadOnlyList<OwnerItem> resumed = View([], [Ledger(reports, resumed: reports[2].Created.AddMinutes(1))]);

        // Assert
        OwnerItem item = paused.Should().ContainSingle().Subject;
        item.Kind.Should().Be(OwnerItemKind.Mandate);
        item.Id.Should().Be(MandateId);
        item.Machine.Should().Be(HomeLaptop);
        item.Project.Should().Be("calizr");
        item.Since.Should().Be(reports[2].Created);
        resumed.Should().BeEmpty();
    }

    // AC-26
    [Fact]
    public void Items_TwoFailuresOnly_NothingWaits()
    {
        // Act and Assert
        View([], [Ledger([Failed(), Failed()])]).Should().BeEmpty();
    }

    // AC-26: the node's escalate on the third report and the pause itself are one item, and it is gone after the resume.
    [Fact]
    public void Items_PausedMandateWithItsEscalate_IsOneItem_GoneAfterResume()
    {
        // Arrange
        MandateReportEntry[] reports = [Failed(), Failed(), Failed()];
        DecisionRecord escalate = Decision(NextId(), reports[2].Id, DecisionKind.Escalate);
        HashSet<EnvelopeId> reportIds = [.. reports.Select(r => r.Id)];
        DateTimeOffset resumeAt = escalate.Created.AddMinutes(5);

        // Act
        IReadOnlyList<OwnerItem> paused = View([escalate], [Ledger(reports)], reportIds);
        IReadOnlyList<OwnerItem> resumed = View([escalate], [Ledger(reports, resumed: resumeAt)], reportIds);

        // Assert
        paused.Should().ContainSingle().Which.Kind.Should().Be(OwnerItemKind.Mandate);
        resumed.Should().BeEmpty();
    }

    // AC-26
    [Fact]
    public void Items_AreOrderedByAge_AndKeysDiffer()
    {
        // Arrange
        DecisionRecord first = Decision(NextId(), NextId(), DecisionKind.Escalate);
        DecisionRecord second = Decision(NextId(), NextId(), DecisionKind.Escalate);

        // Act
        IReadOnlyList<OwnerItem> items = View([second, first]);

        // Assert
        items.Select(i => i.Since).Should().BeInAscendingOrder();
        items.Select(i => i.Key).Distinct().Should().HaveCount(2);
    }
}
