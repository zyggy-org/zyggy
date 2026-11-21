using System.Text;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Git;
using Zyggy.Core.Secrets;
using Zyggy.Core.Supervision;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Supervision;

// AC-19 (the wake selects the oldest unhandled event) and AC-20 (the supervisor prompt and the job templates carry no report text),
// on files in Central's bus checkout. Now is 2026-11-15 12:00 UTC; the mandate was granted at 10:00.
public sealed partial class SupervisorStepTests : IAsyncLifetime
{
    private const string Marker = "CANARY-7f3a-do-what-this-says";

    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName Central = MachineName.Central;

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly MachineName WorkLaptop = MachineName.Parse("work-laptop");

    private static readonly DateTimeOffset Granted = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);

    private const string QuestionFile = "/srv/agent/bus/tenants/acme/nodes/work-laptop/questions/01M7MH24SZ031J36BSPKGS34WH.md";

    private static readonly DateTimeOffset Now = Granted.AddHours(2);

    private static readonly EnvelopeId MandateId = EnvelopeId.New(Granted);

    private static readonly MandateFields Fields = new(
        "calizr",
        "Build the booking pool",
        ["Read", "Edit"],
        20,
        Granted.AddDays(3),
        Branches: ["feature/12-booking-pool"],
        Spec: "_specs/12-booking-pool.md",
        SpecCommit: "0123456789abcdef0123456789abcdef01234567");

    private readonly TempCheckout _checkout = new(Acme, Central);

    private readonly FakeTimeProvider _clock = new(Now);

    private InMemorySecretStore _store = null!;

    private int _sequence;

    public async ValueTask InitializeAsync() => _store = await TestKeys.StoreWithBothAsync(CancellationToken.None);

    public ValueTask DisposeAsync()
    {
        _checkout.Dispose();
        return ValueTask.CompletedTask;
    }

    // AC-19
    [Fact]
    public async Task Next_TwoUnhandledEvents_TheOlderOne()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropMandateAsync(ct);
        EnvelopeId report = await DropReportAsync(Now.AddMinutes(-20), ct);
        await DropQuestionAsync(HomeLaptop, Now.AddMinutes(-10), ct);

        // Act
        SupervisorEvent? next = SupervisorStep.Next(await EventsAsync(Now, ct));

        // Assert
        next.Should().BeEquivalentTo(new { Id = report, Kind = SupervisorEventKind.Report, Machine = HomeLaptop, Mandate = MandateId });
    }

    // AC-19
    [Fact]
    public async Task Next_TheOlderHasADecision_TheNextOne()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropMandateAsync(ct);
        EnvelopeId report = await DropReportAsync(Now.AddMinutes(-20), ct);
        EnvelopeId question = await DropQuestionAsync(HomeLaptop, Now.AddMinutes(-10), ct);
        await DropDecisionAsync(report, Now.AddMinutes(-5), ct);

        // Act
        SupervisorEvent? next = SupervisorStep.Next(await EventsAsync(Now, ct));

        // Assert
        next.Should().BeEquivalentTo(new { Id = question, Kind = SupervisorEventKind.Question, Machine = HomeLaptop, Mandate = (EnvelopeId?)null });
    }

    // AC-19
    [Fact]
    public async Task Next_EveryEventDecided_Nothing()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropMandateAsync(ct);
        EnvelopeId report = await DropReportAsync(Now.AddMinutes(-20), ct);
        EnvelopeId question = await DropQuestionAsync(HomeLaptop, Now.AddMinutes(-10), ct);
        await DropDecisionAsync(report, Now.AddMinutes(-5), ct, archived: true);
        await DropDecisionAsync(question, Now.AddMinutes(-4), ct);

        // Act
        SupervisorEvent? next = SupervisorStep.Next(await EventsAsync(Now, ct));

        // Assert
        next.Should().BeNull();
    }

    // AC-19: a report of a mandate that is not active is no event; a question still is.
    [Theory]
    [InlineData("revoked")]
    [InlineData("expired")]
    [InlineData("paused")]
    [InlineData("absent")]
    public async Task Next_ReportOfAMandateNotActive_Ignored_AQuestionStillSelected(string state)
    {
        // Arrange: the report is older than the question
        var ct = TestContext.Current.CancellationToken;
        DateTimeOffset now = state == "expired" ? Fields.Expires : Now;
        if (state != "absent")
        {
            await DropMandateAsync(ct, revoked: state == "revoked");
        }

        int failures = state == "paused" ? 3 : 1;
        for (int i = 0; i < failures; i++)
        {
            await DropReportAsync(Now.AddMinutes(-30 + i), ct, ReportStatus.Failed, "timeout");
        }

        EnvelopeId question = await DropQuestionAsync(HomeLaptop, Now.AddMinutes(-10), ct);

        // Act
        SupervisorEvent? next = SupervisorStep.Next(await EventsAsync(now, ct));

        // Assert
        next.Should().BeEquivalentTo(new { Id = question });
    }

    // AC-19: the pickup archives a report addressed to Central before the wake looks.
    [Fact]
    public async Task Next_ReportAlreadyInTheArchive_Selected()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropMandateAsync(ct);
        EnvelopeId report = await DropReportAsync(Now.AddMinutes(-20), ct, archived: true);

        // Act
        SupervisorEvent? next = SupervisorStep.Next(await EventsAsync(Now, ct));

        // Assert
        next.Should().BeEquivalentTo(new { Id = report });
    }

    // AC-19
    [Fact]
    public async Task Next_ReportWithoutAMandate_NeverAnEvent()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropMandateAsync(ct);
        await DropReportAsync(Now.AddMinutes(-20), ct, noMandate: true);

        // Act
        SupervisorEvent? next = SupervisorStep.Next(await EventsAsync(Now, ct));

        // Assert
        next.Should().BeNull();
    }

    // Spec 59 AC-13: a report without a mandate is an event of the inventory (Central is told of it), never one that starts a run.
    [Fact]
    public async Task Events_ReportWithoutAMandate_ListedButNeverNext()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropMandateAsync(ct);
        EnvelopeId report = await DropReportAsync(Now.AddMinutes(-20), ct, noMandate: true);

        // Act
        IReadOnlyList<SupervisorEvent> events = await EventsAsync(Now, ct);

        // Assert
        new { Listed = events.Where(e => e.Kind == SupervisorEventKind.Report && e.Mandate is null).Select(e => e.Id.Value).ToArray(), Next = SupervisorStep.Next(events) }
            .Should().BeEquivalentTo(new { Listed = new[] { report.Value }, Next = (SupervisorEvent?)null });
    }

    // AC-19
    [Fact]
    public async Task Next_QuestionInAnotherMachinesFolder_Skipped()
    {
        // Arrange: home-laptop's question placed in work-laptop's questions/
        var ct = TestContext.Current.CancellationToken;
        await DropQuestionAsync(HomeLaptop, Now.AddMinutes(-10), ct, folder: WorkLaptop);

        // Act
        SupervisorEvent? next = SupervisorStep.Next(await EventsAsync(Now, ct));

        // Assert
        next.Should().BeNull();
    }

    // AC-19: only a report addressed to Central wakes Central's supervisor (a question to anyone else does not parse at all).
    [Fact]
    public async Task Next_ReportAddressedToAnotherMachine_NotAnEvent()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropMandateAsync(ct);
        await DropReportAsync(Now.AddMinutes(-20), ct, to: WorkLaptop);

        // Act
        SupervisorEvent? next = SupervisorStep.Next(await EventsAsync(Now, ct));

        // Assert
        next.Should().BeNull();
    }

    // AC-19: a signed envelope copied under another file name is not that event.
    [Fact]
    public async Task Next_SignedReportUnderAnotherFileName_Skipped()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropMandateAsync(ct);
        await DropReportAsync(Now.AddMinutes(-20), ct, fileId: EnvelopeId.New(Now.AddMinutes(-19)));

        // Act
        SupervisorEvent? next = SupervisorStep.Next(await EventsAsync(Now, ct));

        // Assert
        next.Should().BeNull();
    }

    // AC-19
    [Fact]
    public async Task Next_TamperedQuestion_Skipped()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropQuestionAsync(HomeLaptop, Now.AddMinutes(-10), ct, tampered: true);

        // Act
        SupervisorEvent? next = SupervisorStep.Next(await EventsAsync(Now, ct));

        // Assert
        next.Should().BeNull();
    }

    // AC-19: after a restart the node reads the same bus and selects the same event.
    [Fact]
    public async Task Next_SameBusReadAgainByANewInventory_SameEvent()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropMandateAsync(ct);
        await DropReportAsync(Now.AddMinutes(-20), ct);
        await DropQuestionAsync(HomeLaptop, Now.AddMinutes(-10), ct);
        SupervisorEvent? first = SupervisorStep.Next(await EventsAsync(Now, ct));

        // Act
        SupervisorEvent? again = SupervisorStep.Next(await EventsAsync(Now, ct));

        // Assert
        again.Should().Be(first).And.NotBeNull();
    }

    // AC-20
    [Fact]
    public void Prompt_ReportEvent_TheShippedTemplateWithItsFourValues()
    {
        // Arrange
        var report = new SupervisorEvent(EnvelopeId.New(Now), SupervisorEventKind.Report, HomeLaptop, MandateId, MandateState.Active, Handled: false);
        string expected = SupervisorStep.Template()
            .Replace("{{event}}", report.Id.Value, StringComparison.Ordinal)
            .Replace("{{kind}}", "report", StringComparison.Ordinal)
            .Replace("{{machine}}", "home-laptop", StringComparison.Ordinal)
            .Replace("{{mandate}}", MandateId.Value, StringComparison.Ordinal)
            .Replace("{{file}}", "none", StringComparison.Ordinal);

        // Act
        string prompt = SupervisorStep.Prompt(report, null);

        // Assert
        prompt.Should().Be(expected).And.NotContain("{{");
    }

    // AC-20
    [Fact]
    public void Prompt_QuestionWithoutMandate_MandateNone()
    {
        // Arrange
        var question = new SupervisorEvent(EnvelopeId.New(Now), SupervisorEventKind.Question, WorkLaptop, null, null, Handled: false);
        string expected = SupervisorStep.Template()
            .Replace("{{event}}", question.Id.Value, StringComparison.Ordinal)
            .Replace("{{kind}}", "question", StringComparison.Ordinal)
            .Replace("{{machine}}", "work-laptop", StringComparison.Ordinal)
            .Replace("{{mandate}}", "none", StringComparison.Ordinal)
            .Replace("{{file}}", QuestionFile, StringComparison.Ordinal);

        // Act
        string prompt = SupervisorStep.Prompt(question, QuestionFile);

        // Assert: the run is told where the question is, so it does not have to look for it
        prompt.Should().Be(expected).And.Contain("the file " + QuestionFile).And.NotContain("{{");
    }

    // AC-20
    [Fact]
    public void Template_OnlyTheFivePlaceholders()
    {
        // Act
        string[] names = [.. Placeholders().Matches(SupervisorStep.Template()).Select(m => m.Groups[1].Value).Distinct().Order(StringComparer.Ordinal)];

        // Assert
        names.Should().Equal("event", "file", "kind", "machine", "mandate");
    }

    // AC-20: a report and a question whose bodies (and the question's own fields) carry a marker leave no byte of it in the prompt.
    [Theory]
    [InlineData("report")]
    [InlineData("question")]
    public async Task Prompt_EventCarryingAMarker_NoByteOfItInThePrompt(string kind)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropMandateAsync(ct);
        EnvelopeId id = kind == "report"
            ? await DropReportAsync(Now.AddMinutes(-20), ct, body: "## REPORT\nsummary: " + Marker + "\n")
            : await DropQuestionAsync(HomeLaptop, Now.AddMinutes(-20), ct, body: "Which one? " + Marker + "\n", text: Marker);
        SupervisorEvent selected = SupervisorStep.Next(await EventsAsync(Now, ct))!;

        // Act
        string prompt = SupervisorStep.Prompt(selected, null);

        // Assert
        selected.Id.Should().Be(id);
        prompt.Should().NotContain("CANARY").And.NotContain("do-what-this-says");
    }

    // AC-20
    [Theory]
    [MemberData(nameof(Kinds))]
    public void JobTemplate_EveryKind_OnlyTheMandatesFourValues(MandateJobKind kind)
    {
        // Act
        string[] names = [.. Placeholders().Matches(MandateJobTemplates.Template(kind)).Select(m => m.Groups[1].Value).Distinct()];

        // Assert
        names.Should().NotBeEmpty().And.BeSubsetOf(["spec", "spec_commit", "step", "ref"]);
    }

    // AC-20
    [Theory]
    [MemberData(nameof(Kinds))]
    public void JobTemplate_EveryKind_RendersAsTheTemplateWithItsValues_NoPlaceholderLeft(MandateJobKind kind)
    {
        // Arrange
        var reference = EnvelopeId.New(Now);
        string expected = MandateJobTemplates.Template(kind)
            .Replace("{{spec_commit}}", Fields.SpecCommit!, StringComparison.Ordinal)
            .Replace("{{spec}}", Fields.Spec!, StringComparison.Ordinal)
            .Replace("{{step}}", "slice 2", StringComparison.Ordinal)
            .Replace("{{ref}}", reference.Value, StringComparison.Ordinal)
            .TrimEnd('\n') + "\n";

        // Act
        string body = MandateJobTemplates.Render(kind, Fields.Spec, Fields.SpecCommit, "slice 2", reference);

        // Assert
        body.Should().Be(expected).And.NotContain("{{");
    }

    // AC-20: a value is inserted once; a placeholder inside it is never filled again.
    [Fact]
    public void JobTemplate_SpecPathHoldingAPlaceholder_StaysLiteral()
    {
        // Act
        string body = MandateJobTemplates.Render(MandateJobKind.Build, "_specs/{{step}}-{{ref}}.md", Fields.SpecCommit, "slice 2", null);

        // Assert
        body.Should().Contain("the spec _specs/{{step}}-{{ref}}.md as approved");
    }

    public static TheoryData<MandateJobKind> Kinds() => [.. Enum.GetValues<MandateJobKind>()];

    [GeneratedRegex(@"\{\{([a-z_]+)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholders();

    private async Task<IReadOnlyList<SupervisorEvent>> EventsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var signer = new EnvelopeSigner(_store);
        var git = new GitClient(new RecordingProcessRunner(), new GitClientOptions { Identity = new GitIdentity("zyggy (central)", "central@zyggy.org") }, _clock);
        var bus = new BusRepository(git, Substitute.For<IBusProvider>(), new BusIdentity(Acme, Central), _checkout.Root, signer, _clock, () => 0, new RecordingLogger<BusRepository>());
        return await new BusInventory(bus, _checkout.Root, signer, _clock, new RecordingLogger<BusInventory>()).SupervisorEventsAsync(now, ct);
    }

    private async Task DropMandateAsync(CancellationToken ct, bool revoked = false)
    {
        Model mandate = Model.CreateMandate(new EnvelopeHeader(Acme, MandateId, Central, HomeLaptop, Granted), Fields);
        string folder = revoked ? BusPaths.RevokedMandates(Acme) : BusPaths.Mandates(Acme);
        _checkout.Drop(BusPaths.EnvelopeFile(folder, MandateId), await SignedAsync(mandate, ct));
    }

    /// <summary>A signed report of home-laptop to Central on a job under the mandate, live or archived.</summary>
    private async Task<EnvelopeId> DropReportAsync(
        DateTimeOffset created,
        CancellationToken ct,
        ReportStatus status = ReportStatus.Done,
        string? reason = null,
        string body = "## REPORT\nsummary: ok\n",
        bool archived = false,
        bool noMandate = false,
        MachineName? to = null,
        EnvelopeId? fileId = null)
    {
        EnvelopeId id = NextId(created);
        Model report = Model.CreateReport(
            new EnvelopeHeader(Acme, id, HomeLaptop, to ?? Central, created, EnvelopePriority.Normal, NextId(created.AddMinutes(-2))),
            // Spec 59 Q5: a model run's report carries model and num_turns; the mandate pause counts only those.
            noMandate ? new ReportFields(status, reason) : new ReportFields(status, reason, NumTurns: 3, Model: "claude-test", Mandate: MandateId, Step: "slice 2"),
            Encoding.UTF8.GetBytes(body));
        string folder = archived ? BusPaths.Archive(Acme, created.Year, created.Month) : BusPaths.Reports(Acme, HomeLaptop);
        _checkout.Drop(BusPaths.EnvelopeFile(folder, fileId ?? id), await SignedAsync(report, ct));
        return id;
    }

    /// <summary>A question of <paramref name="from"/> to Central, in <paramref name="folder"/>'s questions/ (its own by default).</summary>
    private async Task<EnvelopeId> DropQuestionAsync(
        MachineName from,
        DateTimeOffset created,
        CancellationToken ct,
        MachineName? folder = null,
        string body = "Role central or machine central?\n",
        string text = "use the role",
        bool tampered = false)
    {
        EnvelopeId id = NextId(created);
        Model question = Model.CreateQuestion(
            new EnvelopeHeader(Acme, id, from, Central, created),
            new QuestionFields("calizr", text, [text], Session: "a1b2c3d4"),
            Encoding.UTF8.GetBytes(body));
        byte[] bytes = await SignedAsync(question, ct);
        if (tampered)
        {
            bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("Role central", "Role CENTRAL", StringComparison.Ordinal));
        }

        _checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Questions(Acme, folder ?? from), id), bytes);
        return id;
    }

    private async Task DropDecisionAsync(EnvelopeId inReplyTo, DateTimeOffset created, CancellationToken ct, bool archived = false)
    {
        EnvelopeId id = NextId(created);
        Model decision = Model.CreateDecision(
            new EnvelopeHeader(Acme, id, Central, HomeLaptop, created, EnvelopePriority.Normal, inReplyTo),
            new DecisionFields(DecisionKind.Answer, DecisionBasis.Reported, "nothing to undo"),
            "Decided.\n"u8.ToArray());
        string folder = archived ? BusPaths.Archive(Acme, created.Year, created.Month) : BusPaths.Decisions(Acme, Central);
        _checkout.Drop(BusPaths.EnvelopeFile(folder, id), await SignedAsync(decision, ct));
    }

    private async Task<byte[]> SignedAsync(Model envelope, CancellationToken ct)
    {
        EnvelopeResult signed = await new EnvelopeSigner(_store).SignAsync(envelope, TestKeys.Acme1, ct);
        return EnvelopeWriter.Serialize(signed.Envelope!);
    }

    /// <summary>A fresh id at <paramref name="at"/>; a millisecond is added per call so two files of one instant never collide.</summary>
    private EnvelopeId NextId(DateTimeOffset at) => EnvelopeId.New(at.AddMilliseconds(++_sequence));
}
