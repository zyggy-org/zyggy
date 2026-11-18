using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Git;
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
            Base: Branch));
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

    private void DropRegistry(MachineName machine, bool supervision)
    {
        var document = new RegistryDocument(Acme, machine, RegistryOs.Windows, T.AddMinutes(-1), null, null, [])
        {
            Capabilities = supervision ? [RegistryDocument.SupervisionCapability] : [],
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

    private MandateSubmission Submission()
    {
        BusRepository bus = Repository();
        var signer = new EnvelopeSigner(_store);
        return new MandateSubmission(
            bus,
            new BusInventory(bus, _checkout.Root, signer, _clock, new RecordingLogger<BusInventory>()),
            new JobSubmitter(bus, signer, TestKeys.Acme1, _clock, new RecordingLogger<JobSubmitter>()),
            [WorkLaptop],
            _clock,
            new RecordingLogger<MandateSubmission>());
    }
}
