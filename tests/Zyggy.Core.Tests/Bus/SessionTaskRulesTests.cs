using System.Text;
using System.Text.Json;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Bus;

// Spec 18 slice 14 (AC-31): the pure rules of a task delivered into a live session — which job a turn end may take, the frame the
// session continues with, the Stop-hook answer, the report body and the limits of --why.
public sealed class SessionTaskRulesTests
{
    private const string Marker = "CANARY-3e9b-the-task-body";

    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    private static readonly EnvelopeId MandateId = EnvelopeId.New(Now.AddHours(-2));

    private static Model Job(JobDelivery deliver = JobDelivery.Session, string project = "shop", string? step = "slice 2", string body = "Build slice 2.\n" + Marker + "\n")
    {
        var header = new EnvelopeHeader(Acme, EnvelopeId.New(Now.AddMinutes(-5)), MachineName.Central, HomeLaptop, Now.AddMinutes(-5));
        return Model.CreateJob(header, new JobFields(project, AllowedTools: ["Read"], TimeoutMinutes: 60, Mandate: MandateId, Step: step, Deliver: deliver), Encoding.UTF8.GetBytes(body));
    }

    private static MandateLedger Ledger(bool sessions = true, bool revoked = false)
    {
        var fields = new MandateFields("shop", "Build the shop", ["Read", "Edit"], 5, Now.AddDays(2), Sessions: sessions);
        Model mandate = Model.CreateMandate(new EnvelopeHeader(Acme, MandateId, MachineName.Central, HomeLaptop, Now.AddHours(-2)), fields);
        return new MandateLedger(MandateId, mandate, true, revoked, [], [], null);
    }

    // AC-31
    [Fact]
    public void Frame_SessionJob_NamesTheTaskAndTheTwoFinishingCommands_BodyVerbatimAtTheEnd()
    {
        // Arrange
        Model job = Job();

        // Act
        string frame = SessionTaskRules.Frame(job);

        // Assert
        string id = job.Header.Id.Value;
        new
        {
            Head = frame.StartsWith("Zyggy task " + id + " (mandate " + MandateId.Value + ", step slice 2)", StringComparison.Ordinal),
            Done = frame.Contains("zyggy work --done " + id, StringComparison.Ordinal),
            Failed = frame.Contains("zyggy work --failed " + id + " --why", StringComparison.Ordinal),
            Data = frame.Contains("data", StringComparison.Ordinal),
            Tail = frame.EndsWith("--- task ---\n" + job.BodyText, StringComparison.Ordinal),
        }.Should().BeEquivalentTo(new { Head = true, Done = true, Failed = true, Data = true, Tail = true }, frame);
    }

    // AC-31: the hook's stdout is the Stop-hook answer that makes the session go on with the frame.
    [Fact]
    public void StopJson_Frame_BlockDecisionWithTheFrameAsReason()
    {
        // Arrange
        string frame = SessionTaskRules.Frame(Job());

        // Act
        using JsonDocument json = JsonDocument.Parse(SessionTaskRules.StopJson(frame));

        // Assert
        new { Decision = json.RootElement.GetProperty("decision").GetString(), Reason = json.RootElement.GetProperty("reason").GetString(), Count = json.RootElement.EnumerateObject().Count() }
            .Should().BeEquivalentTo(new { Decision = "block", Reason = frame, Count = 2 });
    }

    // AC-31
    [Fact]
    public void Eligible_SessionJobInsideAMandateWithSessions_Taken()
    {
        // Act
        string? refusal = SessionTaskRules.Refusal(Ledger(), Job(), HomeLaptop, "shop", Now);

        // Assert
        refusal.Should().BeNull();
    }

    // AC-31: without sessions: true in the mandate, for another project, a run job, or a revoked mandate, nothing is delivered.
    [Theory]
    [InlineData("sessions", "sessions")]
    [InlineData("project", "project")]
    [InlineData("run", "deliver")]
    [InlineData("revoked", "revoked")]
    public void Eligible_OutsideTheRules_RefusedNamingTheLimit(string fault, string limit)
    {
        // Arrange
        MandateLedger ledger = Ledger(sessions: fault != "sessions", revoked: fault == "revoked");
        Model job = Job(deliver: fault == "run" ? JobDelivery.Run : JobDelivery.Session);

        // Act
        string? refusal = SessionTaskRules.Refusal(ledger, job, HomeLaptop, fault == "project" ? "other-project" : "shop", Now);

        // Assert
        refusal.Should().Be(limit);
    }

    // AC-31: the report body is fixed fields, plus the one --why line on a machine that is not a work machine.
    [Theory]
    [InlineData(true, false, null, "status: done\nfiles_changed: 3\nhead: 0123abc\n")]
    [InlineData(false, false, "tests red", "status: failed\nfiles_changed: 3\nhead: 0123abc\nwhy: tests red\n")]
    [InlineData(false, true, "tests red", "status: failed\nfiles_changed: 3\nhead: 0123abc\n")]
    public void Body_DoneOrFailed_FixedFieldsAndWhyOnlyOffAWorkMachine(bool done, bool workMachine, string? why, string expected)
    {
        // Act
        string body = SessionTaskRules.Body(done, 3, "0123abc", why, workMachine);

        // Assert
        body.Should().Be(expected);
    }

    // AC-31: a line of 200 characters passes, 201 does not, and a line break never does.
    [Theory]
    [InlineData(200, false, null)]
    [InlineData(201, false, "over 200 characters")]
    [InlineData(10, true, "one line only")]
    public void WhyProblem_Length_AndLineBreak(int length, bool lineBreak, string? expected)
    {
        // Arrange
        string why = new string('a', length) + (lineBreak ? "\nb" : string.Empty);

        // Act
        string? problem = SessionTaskRules.WhyProblem(why);

        // Assert
        problem.Should().Be(expected);
    }
}
