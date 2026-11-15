using System.Text;

using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Git;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Bus;

// Spec 18 slice 4: what the verbs read from a checkout — the open question behind "waiting for Central" (AC-5), the decision lookups
// behind `zyggy ask`, `zyggy decide` and `zyggy decisions` (AC-7, AC-8), the auditor's rules for the two folders (AC-9) — and the
// local state that names the asking session and remembers an unanswered question.
public sealed class AskDecideBoardTests : IAsyncLifetime
{
    private const string Session = "5b3fd3ac";

    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName Central = MachineName.Central;

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly MachineName WorkLaptop = MachineName.Parse("work-laptop");

    private static readonly DateTimeOffset T = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);

    private readonly TempCheckout _checkout = new(Acme, Central);

    private readonly FakeTimeProvider _clock = new(T);

    private InMemorySecretStore _store = null!;

    public async ValueTask InitializeAsync() => _store = await TestKeys.StoreWithBothAsync(CancellationToken.None);

    public ValueTask DisposeAsync()
    {
        _checkout.Dispose();
        return ValueTask.CompletedTask;
    }

    // AC-5
    [Fact]
    public async Task WorkAsync_OpenQuestionNamesTheSession_WaitingForCentral_QuestionListedWithoutText()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var question = EnvelopeId.New(T.AddMinutes(-2));
        await DropStatusAsync(HomeLaptop, Session, ct);
        _checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Questions(Acme, HomeLaptop), question), await QuestionAsync(question, HomeLaptop, Session, ct));

        // Act
        WorkBoard board = await Inventory().WorkAsync([], ct);

        // Assert
        MachineWork machine = board.Machines.Should().ContainSingle().Subject;
        machine.Sessions.Should().ContainSingle().Which.Activity.Should().Be(SessionActivity.WaitingForCentral);
        machine.Questions.Should().Equal(new WorkQuestion(question, "calizr", Session, null, T.AddMinutes(-2)));
    }

    [Theory]
    [InlineData("other000", false, false)]
    [InlineData(Session, true, false)]
    [InlineData(Session, false, true)]
    public async Task WorkAsync_QuestionOfAnotherSessionOrWriterOrTamperedWith_SessionKeepsItsOwnState(string asker, bool writtenByAnotherMachine, bool tampered)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var question = EnvelopeId.New(T.AddMinutes(-2));
        await DropStatusAsync(HomeLaptop, Session, ct);
        byte[] bytes = await QuestionAsync(question, writtenByAnotherMachine ? WorkLaptop : HomeLaptop, asker, ct);
        _checkout.Drop(
            BusPaths.EnvelopeFile(BusPaths.Questions(Acme, HomeLaptop), question),
            tampered ? EnvelopeText.From(bytes).WithValue("project", "other").ToBytes() : bytes);

        // Act
        WorkBoard board = await Inventory().WorkAsync([], ct);

        // Assert
        MachineWork machine = board.Machines.Should().ContainSingle().Subject;
        machine.Sessions.Should().ContainSingle().Which.Activity.Should().Be(SessionActivity.Working);
        machine.Questions.Should().HaveCount(writtenByAnotherMachine || tampered ? 0 : 1);
    }

    [Fact]
    public async Task WorkAsync_QuestionFromAJobAndNoSession_MachineListedWithTheQuestion()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var question = EnvelopeId.New(T.AddMinutes(-2));
        var job = EnvelopeId.New(T.AddMinutes(-9));
        _checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Questions(Acme, HomeLaptop), question), await QuestionAsync(question, HomeLaptop, null, ct, job));

        // Act
        WorkBoard board = await Inventory().WorkAsync([], ct);

        // Assert
        board.Machines.Should().ContainSingle().Which.Questions.Should().Equal(new WorkQuestion(question, "calizr", null, job, T.AddMinutes(-2)));
    }

    // AC-7
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecisionForAsync_LiveOrArchived_TheDecisionWithItsFieldsAndText(bool archived)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var question = EnvelopeId.New(T.AddMinutes(-2));
        var id = EnvelopeId.New(T);
        _checkout.Drop(BusPaths.EnvelopeFile(archived ? BusPaths.Archive(Acme, 2026, 11) : BusPaths.Decisions(Acme, Central), id), await DecisionAsync(id, question, "Use central.", ct));

        // Act
        DecisionRecord? decision = await Inventory().DecisionForAsync(question, ct);

        // Assert
        decision.Should().BeEquivalentTo(new DecisionRecord(id, HomeLaptop, T, question, new DecisionFields(DecisionKind.Answer, DecisionBasis.Reported, "nothing to undo"), "Use central.\n", archived));
    }

    [Fact]
    public async Task DecisionForAsync_TwoDecisionsOnOneQuestion_TheNewest()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var question = EnvelopeId.New(T.AddMinutes(-9));
        var first = EnvelopeId.New(T.AddMinutes(-5));
        var second = EnvelopeId.New(T.AddMinutes(-1));
        _checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Decisions(Acme, Central), first), await DecisionAsync(first, question, "First.", ct));
        _checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Decisions(Acme, Central), second), await DecisionAsync(second, question, "Second.", ct));

        // Act
        DecisionRecord? decision = await Inventory().DecisionForAsync(question, ct);

        // Assert
        decision!.Id.Should().Be(second);
    }

    [Theory]
    [InlineData("tampered")]
    [InlineData("other-question")]
    [InlineData("wrong-file-name")]
    public async Task DecisionForAsync_DecisionThatDoesNotVerifyOrAnswersSomethingElse_Null(string fault)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var question = EnvelopeId.New(T.AddMinutes(-2));
        var id = EnvelopeId.New(T);
        byte[] bytes = await DecisionAsync(id, fault == "other-question" ? EnvelopeId.New(T.AddMinutes(-3)) : question, "Use central.", ct);
        _checkout.Drop(
            BusPaths.EnvelopeFile(BusPaths.Decisions(Acme, Central), fault == "wrong-file-name" ? EnvelopeId.New(T.AddSeconds(1)) : id),
            fault == "tampered" ? EnvelopeText.From(bytes).WithValue("undo", "something else").ToBytes() : bytes);

        // Act
        DecisionRecord? decision = await Inventory().DecisionForAsync(question, ct);

        // Assert
        decision.Should().BeNull();
    }

    // AC-8
    [Fact]
    public async Task DecisionsAsync_LiveAndArchived_NewestFirst_FilteredBySinceAndMandate()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var mandate = EnvelopeId.New(T.AddDays(-3));
        var old = EnvelopeId.New(T.AddDays(-20));
        var underMandate = EnvelopeId.New(T.AddDays(-2));
        var newest = EnvelopeId.New(T);
        _checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Archive(Acme, 2026, 10), old), await DecisionAsync(old, EnvelopeId.New(T.AddDays(-21)), "Old.", ct));
        _checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Decisions(Acme, Central), underMandate), await DecisionAsync(underMandate, EnvelopeId.New(T.AddDays(-2).AddMinutes(-1)), "Under the mandate.", ct, mandate));
        _checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Decisions(Acme, Central), newest), await DecisionAsync(newest, EnvelopeId.New(T.AddMinutes(-1)), "Newest.", ct));
        BusInventory inventory = Inventory();

        // Act
        IReadOnlyList<DecisionRecord> all = await inventory.DecisionsAsync(null, null, ct);
        IReadOnlyList<DecisionRecord> recent = await inventory.DecisionsAsync(T.AddDays(-5), null, ct);
        IReadOnlyList<DecisionRecord> mandated = await inventory.DecisionsAsync(null, mandate, ct);

        // Assert
        all.Select(d => d.Id).Should().Equal(newest, underMandate, old);
        all.Select(d => d.Archived).Should().Equal(false, false, true);
        recent.Select(d => d.Id).Should().Equal(newest, underMandate);
        mandated.Select(d => d.Id).Should().Equal(underMandate);
    }

    [Fact]
    public async Task FindDecidableAsync_OpenQuestion_ItsMachineProjectAndTheVerifiedQuestion()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var question = EnvelopeId.New(T.AddMinutes(-2));
        _checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Questions(Acme, HomeLaptop), question), await QuestionAsync(question, HomeLaptop, Session, ct));

        // Act
        DecisionTarget? target = await Inventory().FindDecidableAsync(question, ct);

        // Assert
        target!.Machine.Should().Be(HomeLaptop);
        target.Project.Should().Be("calizr");
        target.Question!.Header.Id.Should().Be(question);
    }

    [Theory]
    [InlineData("claimed-job")]
    [InlineData("report")]
    [InlineData("archived-question")]
    public async Task FindDecidableAsync_JobReportOrArchivedQuestion_ItsMachine_NothingToArchive(string kind)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var id = EnvelopeId.New(T.AddMinutes(-2));
        (string folder, byte[] bytes) = kind switch
        {
            "claimed-job" => (BusPaths.Claimed(Acme, HomeLaptop), await TestEnvelopes.SignedJobBytesAsync(Central, HomeLaptop, _store, TestKeys.Acme1, "probe\n", ct, id: id)),
            "report" => (BusPaths.Reports(Acme, HomeLaptop), await TestEnvelopes.SignedReportBytesAsync(HomeLaptop, Central, EnvelopeId.New(T.AddMinutes(-30)), _store, TestKeys.Acme1, "ok\n", ct, id: id, created: T.AddMinutes(-2))),
            _ => (BusPaths.Archive(Acme, 2026, 11), await QuestionAsync(id, HomeLaptop, Session, ct)),
        };
        _checkout.Drop(BusPaths.EnvelopeFile(folder, id), bytes);

        // Act
        DecisionTarget? target = await Inventory().FindDecidableAsync(id, ct);

        // Assert
        target!.Machine.Should().Be(HomeLaptop);
        target.Question.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FindDecidableAsync_UnknownIdOrAQuestionAnotherMachineWroteIntoTheFolder_Null(bool foreignQuestion)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var question = EnvelopeId.New(T.AddMinutes(-2));
        if (foreignQuestion)
        {
            _checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Questions(Acme, HomeLaptop), question), await QuestionAsync(question, WorkLaptop, Session, ct));
        }

        // Act
        DecisionTarget? target = await Inventory().FindDecidableAsync(question, ct);

        // Assert
        target.Should().BeNull();
    }

    // AC-9
    [Fact]
    public async Task Audit_QuestionFromItsMachineAndDecisionInCentralsFolder_Ok()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var question = EnvelopeId.New(T.AddMinutes(-2));
        var decision = EnvelopeId.New(T);
        _checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Questions(Acme, HomeLaptop), question), await QuestionAsync(question, HomeLaptop, Session, ct));
        _checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Decisions(Acme, Central), decision), await DecisionAsync(decision, question, "Use central.", ct));

        // Act
        AuditReport report = await Auditor().RunAsync(null, ct);

        // Assert
        report.Entries.Should().HaveCount(2).And.OnlyContain(e => e.Outcome == AuditOutcome.Ok);
    }

    [Fact]
    public async Task Audit_QuestionFromAnotherMachine_WriterMismatchFrom()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var question = EnvelopeId.New(T);
        _checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Questions(Acme, HomeLaptop), question), await QuestionAsync(question, WorkLaptop, Session, ct));

        // Act
        AuditReport report = await Auditor().RunAsync(null, ct);

        // Assert
        AuditEntry entry = report.Entries.Should().ContainSingle().Subject;
        (entry.Outcome, entry.Field).Should().Be((AuditOutcome.WriterMismatch, "from"));
    }

    [Fact]
    public async Task Audit_DecisionInALaptopsFolder_WriterMismatch()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var decision = EnvelopeId.New(T);
        _checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Decisions(Acme, HomeLaptop), decision), await DecisionAsync(decision, EnvelopeId.New(T.AddMinutes(-1)), "Use central.", ct));

        // Act
        AuditReport report = await Auditor().RunAsync(null, ct);

        // Assert
        report.Entries.Should().ContainSingle().Which.Outcome.Should().Be(AuditOutcome.WriterMismatch);
    }

    [Theory]
    [InlineData(true, "decision in questions/")]
    [InlineData(false, "question in decisions/")]
    public async Task Audit_DecisionInQuestionsOrQuestionInDecisions_TypeFolderMismatch(bool decisionInQuestions, string detail)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var id = EnvelopeId.New(T);
        (string folder, byte[] bytes) = decisionInQuestions
            ? (BusPaths.Questions(Acme, Central), await DecisionAsync(id, EnvelopeId.New(T.AddMinutes(-1)), "Use central.", ct))
            : (BusPaths.Decisions(Acme, Central), await QuestionAsync(id, Central, Session, ct));
        _checkout.Drop(BusPaths.EnvelopeFile(folder, id), bytes);

        // Act
        AuditReport report = await Auditor().RunAsync(null, ct);

        // Assert
        AuditEntry entry = report.Entries.Should().ContainSingle().Subject;
        (entry.Outcome, entry.Field, entry.Detail).Should().Be((AuditOutcome.TypeFolderMismatch, "type", detail));
    }

    [Fact]
    public void LatestSession_TwoSessionsOfTheProject_TheOneHeardFromLast_OtherProjectsIgnored()
    {
        // Arrange
        var store = new WorkSessionStore(_checkout.Root, Acme);
        store.Save("aaaa1111", new WorkSessionState(T.AddHours(-3), T.AddMinutes(-40), Project: "calizr"));
        store.Save("bbbb2222", new WorkSessionState(T.AddMinutes(-20), Project: "calizr"));
        store.Save("cccc3333", new WorkSessionState(T.AddMinutes(-1), T.AddMinutes(-1), Project: "other"));
        store.Save("dddd4444", new WorkSessionState(T));

        // Act
        string? session = store.LatestSession("calizr");

        // Assert
        session.Should().Be("bbbb2222");
        store.LatestSession("unknown").Should().BeNull();
    }

    [Fact]
    public void Pending_OldestFirst_EntriesOlderThanThirtyDaysDropped()
    {
        // Arrange
        var store = new AskedStore(_checkout.Root, Acme);
        var stale = EnvelopeId.New(T.AddDays(-31));
        var older = EnvelopeId.New(T.AddDays(-2));
        var newer = EnvelopeId.New(T.AddMinutes(-5));
        store.Add(newer, "calizr");
        store.Add(stale, "calizr");
        store.Add(older, "other");

        // Act
        IReadOnlyList<(EnvelopeId Question, string Project)> pending = store.Pending(T);

        // Assert
        pending.Should().Equal((older, "other"), (newer, "calizr"));
        store.Pending(T).Should().HaveCount(2);
    }

    private async Task DropStatusAsync(MachineName machine, string session, CancellationToken ct)
    {
        var id = EnvelopeId.New(T);
        Model status = Model.CreateStatus(new EnvelopeHeader(Acme, id, machine, Central, T), new StatusFields("calizr", session, T.AddHours(-1), T));
        _checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Status(Acme, machine), id), await SignedAsync(status, ct));
    }

    private Task<byte[]> QuestionAsync(EnvelopeId id, MachineName from, string? session, CancellationToken ct, EnvelopeId? job = null) =>
        SignedAsync(
            Model.CreateQuestion(
                new EnvelopeHeader(Acme, id, from, Central, id.Timestamp),
                new QuestionFields("calizr", "keep the name central", [], session, job),
                Encoding.UTF8.GetBytes("Which role name does the booking pool use?\n")),
            ct);

    private Task<byte[]> DecisionAsync(EnvelopeId id, EnvelopeId inReplyTo, string text, CancellationToken ct, EnvelopeId? mandate = null) =>
        SignedAsync(
            Model.CreateDecision(
                new EnvelopeHeader(Acme, id, Central, HomeLaptop, id.Timestamp, EnvelopePriority.Normal, inReplyTo),
                new DecisionFields(DecisionKind.Answer, DecisionBasis.Reported, "nothing to undo", mandate),
                Encoding.UTF8.GetBytes(text + "\n")),
            ct);

    private async Task<byte[]> SignedAsync(Model envelope, CancellationToken ct)
    {
        EnvelopeResult signed = await new EnvelopeSigner(_store).SignAsync(envelope, TestKeys.Acme1, ct);
        return EnvelopeWriter.Serialize(signed.Envelope!);
    }

    private BusRepository Bus(EnvelopeSigner signer)
    {
        var git = new GitClient(new RecordingProcessRunner(), new GitClientOptions { Identity = new GitIdentity("zyggy (central)", "central@zyggy.org") }, _clock);
        return new BusRepository(git, Substitute.For<IBusProvider>(), new BusIdentity(Acme, Central), _checkout.Root, signer, _clock, () => 0, new RecordingLogger<BusRepository>());
    }

    private BusInventory Inventory()
    {
        var signer = new EnvelopeSigner(_store);
        return new BusInventory(Bus(signer), _checkout.Root, signer, _clock, new RecordingLogger<BusInventory>());
    }

    private BusAuditor Auditor()
    {
        var signer = new EnvelopeSigner(_store);
        return new BusAuditor(Bus(signer), _checkout.Root, signer, _clock, new RecordingLogger<BusAuditor>());
    }
}
