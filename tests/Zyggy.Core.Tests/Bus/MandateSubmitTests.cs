using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Git;
using Zyggy.Core.Models;
using Zyggy.Core.Registry;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Bus;

// AC-10 and AC-14 (unit side): Central's submit under a mandate reads the mandate, the target's registry file and the work-machine
// list from its own checkout, refuses outside the mandate before anything is signed, and otherwise writes a job whose body is the
// shipped template and whose tools and project are the mandate's.
public sealed class MandateSubmitTests : IAsyncLifetime
{
    private const string Spec = "_specs/12-booking-pool.md";

    private const string SpecCommit = "0123456789abcdef0123456789abcdef01234567";

    private const string Branch = "feature/12-booking-pool";

    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName Central = MachineName.Central;

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly MachineName WorkLaptop = MachineName.Parse("work-laptop");

    private static readonly DateTimeOffset T = new(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);

    private static readonly EnvelopeId MandateId = EnvelopeId.New(T.AddHours(-1));

    private readonly TempCheckout _checkout = new(Acme, Central);

    private readonly RecordingProcessRunner _processes = new();

    private readonly IBusProvider _provider = Substitute.For<IBusProvider>();

    private readonly FakeTimeProvider _clock = new(T);

    private InMemorySecretStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _provider.PullAsync(Arg.Any<CancellationToken>()).Returns(new BusSyncResult(true, string.Empty));
        _provider.PushAsync(Arg.Any<CancellationToken>()).Returns(new PushSucceeded());
        _store = await TestKeys.StoreWithBothAsync(CancellationToken.None);
    }

    public ValueTask DisposeAsync()
    {
        _checkout.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task SubmitAsync_InsideTheMandate_JobCarriesTheTemplateTheCeilingAndTheMandateFields()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropMandateAsync(Fields(), HomeLaptop, ct);
        DropRegistry(HomeLaptop, supervision: true);
        EnvelopeId reference = EnvelopeId.New(T.AddMinutes(-5));

        // Act
        MandateSubmitOutcome outcome = await Submission().SubmitAsync(new MandateJobRequest(HomeLaptop, MandateId, MandateJobKind.Correct, "slice 2", reference, Branch), ct);

        // Assert
        outcome.Verdict.Allowed.Should().BeTrue(outcome.Verdict.Detail);
        var submitted = outcome.Result.Should().BeOfType<Submitted>().Subject;
        Model job = (await new EnvelopeSigner(_store).ParseAndVerifyAsync(_checkout.Read(submitted.RelativePath), Acme, ct)).Envelope!;
        job.BodyText.Should().Be(MandateJobTemplates.Render(MandateJobKind.Correct, Spec, SpecCommit, "slice 2", reference));
        job.Job.Should().BeEquivalentTo(new JobFields(
            "calizr",
            Worktree: true,
            AllowedTools: ["Read", "Edit", "Bash(dotnet test *)"],
            Mandate: MandateId,
            Step: "slice 2",
            Base: Branch,
            JobType: "correct"));
    }

    // Spec 59 AC-9 (Q6): a mandate step may continue a failed step's branch — same step name, a new job of the mandate, its own turn limit.
    [Fact]
    public async Task SubmitAsync_ContinueUnderTheMandate_JobCarriesContinueMaxTurnsAndTheSameStep_CountedAsANewJob()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropMandateAsync(Fields(), HomeLaptop, ct);
        DropRegistry(HomeLaptop, supervision: true);
        MandateSubmitOutcome first = await Submission().SubmitAsync(new MandateJobRequest(HomeLaptop, MandateId, MandateJobKind.Build, "slice 2", Base: Branch), ct);
        EnvelopeId earlier = first.Result.Should().BeOfType<Submitted>().Subject.Id;

        // Act
        MandateSubmitOutcome outcome = await Submission().SubmitAsync(
            new MandateJobRequest(HomeLaptop, MandateId, MandateJobKind.Build, "slice 2", Base: Branch, Continue: earlier, MaxTurns: 120), ct);

        // Assert
        var submitted = outcome.Result.Should().BeOfType<Submitted>().Subject;
        JobFields job = (await new EnvelopeSigner(_store).ParseAndVerifyAsync(_checkout.Read(submitted.RelativePath), Acme, ct)).Envelope!.Job!;
        BusRepository bus = Repository();
        MandateLedger ledger = await new BusInventory(bus, _checkout.Root, new EnvelopeSigner(_store), _clock, new RecordingLogger<BusInventory>()).MandateLedgerAsync(MandateId, ct);
        new { job.Continue, job.MaxTurns, job.Step, job.Base, Counted = ledger.Counted.Count(j => j.Step == "slice 2") }
            .Should().BeEquivalentTo(new { Continue = earlier, MaxTurns = 120, Step = "slice 2", Base = Branch, Counted = 2 });
    }

    // Spec 59 slice 6 tightening (Central, 2026-11-21): under a mandate, continue only from a job of the same mandate and step — refused
    // at submit with the limit named, nothing written.
    [Fact]
    public async Task SubmitAsync_ContinueFromAJobOfAnotherStep_RefusedContinue_NothingWritten()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropMandateAsync(Fields(), HomeLaptop, ct);
        DropRegistry(HomeLaptop, supervision: true);
        MandateSubmitOutcome first = await Submission().SubmitAsync(new MandateJobRequest(HomeLaptop, MandateId, MandateJobKind.Build, "slice 1", Base: Branch), ct);
        EnvelopeId earlier = first.Result.Should().BeOfType<Submitted>().Subject.Id;
        int commits = _processes.CallsOf("commit").Count();

        // Act
        MandateSubmitOutcome outcome = await Submission().SubmitAsync(
            new MandateJobRequest(HomeLaptop, MandateId, MandateJobKind.Build, "slice 2", Base: Branch, Continue: earlier), ct);

        // Assert
        new { outcome.Verdict.Limit, outcome.Verdict.Detail, outcome.Result, Commits = _processes.CallsOf("commit").Count() }
            .Should().BeEquivalentTo(new
            {
                Limit = MandateLimit.Continue,
                Detail = "continue: " + earlier.Value + " is not a job of this mandate's step 'slice 2'",
                Result = (SubmitResult?)null,
                Commits = commits,
            });
    }

    [Fact]
    public async Task SubmitAsync_SessionTask_DeliverSessionNoWorktreeSixtyMinutes()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropMandateAsync(Fields() with { Sessions = true }, HomeLaptop, ct);
        DropRegistry(HomeLaptop, supervision: true);

        // Act
        MandateSubmitOutcome outcome = await Submission().SubmitAsync(new MandateJobRequest(HomeLaptop, MandateId, MandateJobKind.Build, "slice 2", Session: true), ct);

        // Assert
        var submitted = outcome.Result.Should().BeOfType<Submitted>().Subject;
        JobFields job = (await new EnvelopeSigner(_store).ParseAndVerifyAsync(_checkout.Read(submitted.RelativePath), Acme, ct)).Envelope!.Job!;
        (job.Deliver, job.Worktree, job.TimeoutMinutes).Should().Be((JobDelivery.Session, false, 60));
    }

    [Theory]
    [InlineData("capability", MandateLimit.Capability)]
    [InlineData("work-machine", MandateLimit.WorkMachine)]
    [InlineData("grant", MandateLimit.Grant)]
    [InlineData("absent", MandateLimit.Absent)]
    [InlineData("base", MandateLimit.Base)]
    public async Task SubmitAsync_OutsideTheMandate_RefusedNamingTheLimit_NothingWritten(string fault, MandateLimit limit)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        MachineName to = fault == "work-machine" ? WorkLaptop : HomeLaptop;
        if (fault != "absent")
        {
            await DropMandateAsync(Fields(), to, ct);
        }

        DropRegistry(to, supervision: fault != "capability");
        var request = new MandateJobRequest(
            to,
            MandateId,
            fault == "grant" ? MandateJobKind.Merge : MandateJobKind.Build,
            "slice 2",
            Base: fault == "base" ? "main" : Branch);

        // Act
        MandateSubmitOutcome outcome = await Submission().SubmitAsync(request, ct);

        // Assert
        (outcome.Verdict.Limit, outcome.Result).Should().Be((limit, (SubmitResult?)null));
        _processes.CallsOf("commit").Should().BeEmpty();
        _checkout.FilesUnder(BusPaths.Jobs(Acme, to)).Should().BeEmpty();
    }

    [Fact]
    public async Task SubmitAsync_NoRegistryFile_RefusedCapability()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropMandateAsync(Fields(), HomeLaptop, ct);

        // Act
        MandateSubmitOutcome outcome = await Submission().SubmitAsync(new MandateJobRequest(HomeLaptop, MandateId, MandateJobKind.Build, "slice 2"), ct);

        // Assert
        outcome.Verdict.Limit.Should().Be(MandateLimit.Capability);
    }

    [Fact]
    public async Task BuildAsync_JobWithTheFourMandateFields_ReadsBackAndIsBuilt()
    {
        // Arrange
        ProjectReference.TryParse("calizr", out ProjectReference project, out _).Should().BeTrue();
        var request = new JobRequest(HomeLaptop, project, null, 60, ["Read"], EnvelopePriority.Normal, "Build.\n"u8.ToArray())
        {
            Mandate = MandateId,
            Step = "slice 2",
            Base = Branch,
            Deliver = JobDelivery.Session,
        };

        // Act
        BuildResult result = await Submitter().BuildAsync(request, TestContext.Current.CancellationToken);

        // Assert
        JobFields job = result.Should().BeOfType<Built>().Subject.Envelope.Job!;
        (job.Mandate, job.Step, job.Base, job.Deliver).Should().Be((MandateId, "slice 2", Branch, JobDelivery.Session));
    }

    [Theory]
    [InlineData(MandateJobKind.Brief)]
    [InlineData(MandateJobKind.Build)]
    [InlineData(MandateJobKind.Verify)]
    [InlineData(MandateJobKind.Correct)]
    [InlineData(MandateJobKind.Merge)]
    [InlineData(MandateJobKind.Release)]
    [InlineData(MandateJobKind.Deploy)]
    public void Render_EveryKind_FillsEachPlaceholder_EndsWithOneNewline(MandateJobKind kind)
    {
        // Act
        string body = MandateJobTemplates.Render(kind, Spec, SpecCommit, "slice 2", MandateId);

        // Assert
        body.Should().NotContain("{{").And.EndWith(".\n").And.NotEndWith("\n\n").And.StartWith("Kind: " + EnvelopeWire.ToWire(kind) + ". Step: slice 2.");
    }

    [Fact]
    public void Render_Correct_NamesTheSpecTheCommitAndTheReference()
    {
        // Act
        string body = MandateJobTemplates.Render(MandateJobKind.Correct, Spec, SpecCommit, "slice 2", MandateId);

        // Assert
        body.Should().Contain(Spec).And.Contain(SpecCommit).And.Contain(MandateId.Value);
    }

    private static MandateFields Fields() => new(
        "calizr",
        "Build the booking pool",
        ["Read", "Edit", "Bash(dotnet test *)"],
        3,
        T.AddDays(3),
        Spec,
        SpecCommit,
        [Branch]);

    private async Task DropMandateAsync(MandateFields fields, MachineName to, CancellationToken ct)
    {
        DateTimeOffset at = MandateId.Timestamp;
        Model mandate = Model.CreateMandate(new EnvelopeHeader(Acme, MandateId, Central, to, at), fields);
        Model signed = (await new EnvelopeSigner(_store).SignAsync(mandate, TestKeys.Acme1, ct)).Envelope!;
        _checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Mandates(Acme), MandateId), EnvelopeWriter.Serialize(signed));
    }

    private void DropRegistry(MachineName machine, bool supervision, bool model = false)
    {
        var document = new RegistryDocument(Acme, machine, RegistryOs.Windows, T.AddMinutes(-1), null, null, [])
        {
            Capabilities = [.. supervision ? [RegistryDocument.SupervisionCapability] : Array.Empty<string>(), .. model ? [RegistryDocument.ModelCapability] : Array.Empty<string>()],
        };
        _checkout.Drop(BusPaths.Registry(Acme, machine), document.Serialize());
    }

    private BusRepository Repository()
    {
        var git = new GitClient(_processes, new GitClientOptions { Identity = new GitIdentity("zyggy (central)", "central@zyggy.org") }, _clock);
        return new BusRepository(git, _provider, new BusIdentity(Acme, Central), _checkout.Root, new EnvelopeSigner(_store), _clock, () => 0.5, new RecordingLogger<BusRepository>());
    }

    private JobSubmitter Submitter() =>
        new(Repository(), new EnvelopeSigner(_store), TestKeys.Acme1, _clock, new RecordingLogger<JobSubmitter>());

    // Spec 59 AC-6: a base the origin does not have is refused when Central holds a checkout of the project.
    [Fact]
    public async Task SubmitAsync_BaseAbsentOnTheOriginOfALocalCheckout_RefusedBase_NothingWritten()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropMandateAsync(Fields(), HomeLaptop, ct);
        DropRegistry(HomeLaptop, supervision: true);
        _processes.On("ls-remote", RecordingProcessRunner.Fail(2, ""));

        // Act
        MandateSubmitOutcome outcome = await Submission(LocalCalizr).SubmitAsync(new MandateJobRequest(HomeLaptop, MandateId, MandateJobKind.Build, "slice 2", Base: Branch), ct);

        // Assert
        new { outcome.Verdict, outcome.Result, Jobs = _checkout.FilesUnder(BusPaths.Jobs(Acme, HomeLaptop)).Count }.Should().BeEquivalentTo(new
        {
            Verdict = new MandateVerdict(MandateLimit.Base, "'" + Branch + "' does not exist on the origin of calizr"),
            Result = (SubmitResult?)null,
            Jobs = 0,
        });
    }

    // Spec 59 AC-6 (Q5): Central without the project submits; the node's report is the safety net.
    [Fact]
    public async Task SubmitAsync_BaseWithoutALocalCheckout_NoOriginAsked_Submitted()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropMandateAsync(Fields(), HomeLaptop, ct);
        DropRegistry(HomeLaptop, supervision: true);
        _processes.On("ls-remote", RecordingProcessRunner.Fail(2, ""));

        // Act
        MandateSubmitOutcome outcome = await Submission(_ => null).SubmitAsync(new MandateJobRequest(HomeLaptop, MandateId, MandateJobKind.Build, "slice 2", Base: Branch), ct);

        // Assert
        new { Submitted = outcome.Result is Submitted, Asked = _processes.CallsOf("ls-remote").Count() }.Should().BeEquivalentTo(new { Submitted = true, Asked = 0 });
    }

    // Spec 59 AC-6: a base the origin has passes the check.
    [Fact]
    public async Task SubmitAsync_BasePresentOnTheOriginOfALocalCheckout_Submitted()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropMandateAsync(Fields(), HomeLaptop, ct);
        DropRegistry(HomeLaptop, supervision: true);
        _processes.On("ls-remote", RecordingProcessRunner.Ok("abc\trefs/heads/" + Branch + "\n"));

        // Act
        MandateSubmitOutcome outcome = await Submission(LocalCalizr).SubmitAsync(new MandateJobRequest(HomeLaptop, MandateId, MandateJobKind.Build, "slice 2", Base: Branch), ct);

        // Assert
        outcome.Result.Should().BeOfType<Submitted>();
    }

    // Spec 50 AC-10: under a mandate the model comes from the owner's table by kind; job_type is the kind.
    [Fact]
    public async Task SubmitAsync_ModelTableRowForTheKind_JobCarriesItsModelAndTheKindAsJobType()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropMandateAsync(Fields(), HomeLaptop, ct);
        DropRegistry(HomeLaptop, supervision: true, model: true);
        var table = new ModelTable(["claude-opus-5-5"], [new ModelTableRow("build", "claude-opus-5-5")], null);

        // Act
        MandateSubmitOutcome outcome = await Submission().SubmitAsync(new MandateJobRequest(HomeLaptop, MandateId, MandateJobKind.Build, "slice 2", Base: Branch), table, ct);

        // Assert
        var submitted = outcome.Result.Should().BeOfType<Submitted>().Subject;
        JobFields job = (await new EnvelopeSigner(_store).ParseAndVerifyAsync(_checkout.Read(submitted.RelativePath), Acme, ct)).Envelope!.Job!;
        new { job.Model, job.JobType }.Should().BeEquivalentTo(new { Model = "claude-opus-5-5", JobType = "build" });
    }

    // Spec 50 AC-10: a kind the table has no row for runs on the node default; job_type still names the kind.
    [Fact]
    public async Task SubmitAsync_ModelTableWithoutTheKind_NoModelJobTypeIsTheKind()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropMandateAsync(Fields(), HomeLaptop, ct);
        DropRegistry(HomeLaptop, supervision: true);
        var table = new ModelTable(["claude-opus-5-5"], [new ModelTableRow("build", "claude-opus-5-5")], null);

        // Act
        MandateSubmitOutcome outcome = await Submission().SubmitAsync(new MandateJobRequest(HomeLaptop, MandateId, MandateJobKind.Verify, "slice 2", Base: Branch), table, ct);

        // Assert
        var submitted = outcome.Result.Should().BeOfType<Submitted>().Subject;
        JobFields job = (await new EnvelopeSigner(_store).ParseAndVerifyAsync(_checkout.Read(submitted.RelativePath), Acme, ct)).Envelope!.Job!;
        new { job.Model, job.JobType }.Should().BeEquivalentTo(new { Model = (string?)null, JobType = "verify" });
    }

    // Spec 50 AC-10 and the edge case "deliver: session task with model" (slice 5 verify correction): a live session's model is the
    // owner's, so a mandate session task carries no model even when the table has a row for its kind (job_type still names the kind).
    [Fact]
    public async Task SubmitAsync_SessionTaskWithATableRowForTheKind_NoModel()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropMandateAsync(Fields() with { Sessions = true }, HomeLaptop, ct);
        DropRegistry(HomeLaptop, supervision: true, model: true);
        var table = new ModelTable(["claude-opus-5-5"], [new ModelTableRow("build", "claude-opus-5-5")], null);

        // Act
        MandateSubmitOutcome outcome = await Submission().SubmitAsync(new MandateJobRequest(HomeLaptop, MandateId, MandateJobKind.Build, "slice 2", Session: true), table, ct);

        // Assert
        var submitted = outcome.Result.Should().BeOfType<Submitted>().Subject;
        JobFields job = (await new EnvelopeSigner(_store).ParseAndVerifyAsync(_checkout.Read(submitted.RelativePath), Acme, ct)).Envelope!.Job!;
        new { job.Deliver, job.Model, job.JobType }.Should().BeEquivalentTo(new { Deliver = JobDelivery.Session, Model = (string?)null, JobType = "build" });
    }

    // Spec 50 AC-10 with AC-7: a table model to a node without the capability model is refused, nothing written — never its default.
    [Fact]
    public async Task SubmitAsync_ModelTableRowToANodeWithoutCapabilityModel_RefusedNothingWritten()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await DropMandateAsync(Fields(), HomeLaptop, ct);
        DropRegistry(HomeLaptop, supervision: true);
        var table = new ModelTable(["claude-opus-5-5"], [new ModelTableRow("build", "claude-opus-5-5")], null);

        // Act
        MandateSubmitOutcome outcome = await Submission().SubmitAsync(new MandateJobRequest(HomeLaptop, MandateId, MandateJobKind.Build, "slice 2", Base: Branch), table, ct);

        // Assert
        new { Field = (outcome.Result as SubmitRefused)?.Field, Jobs = _checkout.FilesUnder(BusPaths.Jobs(Acme, HomeLaptop)).Count }
            .Should().BeEquivalentTo(new { Field = "capability", Jobs = 0 });
    }

    private string? LocalCalizr(string project) => project == "calizr" ? Path.Combine(_checkout.Root, "calizr") : null;

    private MandateSubmission Submission(Func<string, string?>? localProject = null)
    {
        BusRepository bus = Repository();
        var signer = new EnvelopeSigner(_store);
        var git = new GitClient(_processes, new GitClientOptions { Identity = new GitIdentity("zyggy (central)", "central@zyggy.org") }, _clock);
        return new MandateSubmission(
            bus,
            new BusInventory(bus, _checkout.Root, signer, _clock, new RecordingLogger<BusInventory>()),
            new JobSubmitter(bus, signer, TestKeys.Acme1, _clock, new RecordingLogger<JobSubmitter>()),
            [WorkLaptop],
            _clock,
            new RecordingLogger<MandateSubmission>(),
            localProject is null ? null : new OriginBranchCheck(git, (name, _) => localProject(name)));
    }
}
