using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Envelope;

// AC-1 (mandate part): the mandate parses, signs and verifies; a missing or misshapen field is refused by name; a job and a
// report carry the mandate's id and step; the existing golden files are untouched.
public sealed class MandateEnvelopeTests
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly DateTimeOffset T = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);

    private static readonly EnvelopeId MandateId = EnvelopeId.Parse("01M7F1M0FFFFFFFFFFFFFFFFFF");

    private static EnvelopeText Mandate => EnvelopeText.From(Golden.Md("mandate"));

    private static MandateFields Fields => new(
        "calizr",
        "Build the booking pool as the approved spec says",
        ["Read", "Edit", "Bash(dotnet test *)"],
        12,
        T.AddDays(3),
        "_specs/12-booking-pool.md",
        "9f2c1ab0c3d4e5f60718293a4b5c6d7e8f901234",
        ["feature/12-booking-pool"]);

    [Fact]
    public void Parse_MandateGolden_TypedFields()
    {
        // Act
        EnvelopeResult result = EnvelopeParser.Parse(Golden.Md("mandate"), Acme);

        // Assert
        result.IsAccepted.Should().BeTrue(result.Rejection?.ToString());
        Model mandate = result.Envelope!;
        mandate.Type.Should().Be(EnvelopeType.Mandate);
        mandate.Header.From.Should().Be(MachineName.Central);
        mandate.Header.To.Should().Be(HomeLaptop);
        mandate.Mandate.Should().BeEquivalentTo(Fields with { Grants = [MandateGrant.Accept] });
        mandate.Mandate!.MaxJobsPerHour.Should().Be(6);
        mandate.Mandate.MaxJobsPerStep.Should().Be(4);
        mandate.Mandate.Sessions.Should().BeFalse();
        mandate.Body.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Parse_JobAndReportGoldens_CarryTheMandateAndStep()
    {
        // Act
        Model job = EnvelopeParser.Parse(Golden.Md("job-mandate"), Acme).Envelope!;
        Model report = EnvelopeParser.Parse(Golden.Md("report-mandate"), Acme).Envelope!;

        // Assert
        (job.Job!.Mandate, job.Job.Step, job.Job.Base, job.Job.Deliver).Should().Be((MandateId, "slice 2", "feature/12-booking-pool", JobDelivery.Run));
        (report.Report!.Mandate, report.Report.Step).Should().Be((MandateId, "slice 2"));
    }

    [Theory]
    [InlineData("mandate")]
    [InlineData("job-mandate")]
    [InlineData("report-mandate")]
    public async Task Goldens_CanonicalBytesAndSignatureEqualTheFiles_AndVerify(string name)
    {
        // Arrange
        var signer = new EnvelopeSigner(await TestKeys.StoreWithBothAsync(TestContext.Current.CancellationToken));
        Model envelope = EnvelopeParser.Parse(Golden.Md(name), Acme).Envelope!;

        // Act
        byte[] canonical = EnvelopeSigner.Canonicalize(envelope);
        EnvelopeResult signed = await signer.SignAsync(envelope, TestKeys.Acme1, TestContext.Current.CancellationToken);
        EnvelopeResult verified = await signer.ParseAndVerifyAsync(Golden.Md(name), Acme, TestContext.Current.CancellationToken);

        // Assert
        canonical.Should().Equal(Golden.Canonical(name));
        signed.Envelope!.Signature.Should().Be("hmac-sha256:" + Golden.Sig(name));
        verified.IsAccepted.Should().BeTrue(verified.Rejection?.ToString());
    }

    [Theory]
    [InlineData("project")]
    [InlineData("goal")]
    [InlineData("allowed_tools")]
    [InlineData("max_jobs")]
    [InlineData("expires")]
    public void Parse_MandatoryFieldRemoved_MissingFieldNamingIt(string key)
    {
        // Act
        EnvelopeResult result = EnvelopeParser.Parse(Mandate.WithoutKey(key).ToBytes(), Acme);

        // Assert
        result.Rejection!.Reason.Should().Be(EnvelopeRejectionReason.MissingField);
        result.Rejection.Field.Should().Be(key);
    }

    [Theory]
    [InlineData("from", "home-laptop")]
    [InlineData("project", "Calizr")]
    [InlineData("spec", "/etc/passwd")]
    [InlineData("spec", "../other/spec.md")]
    [InlineData("spec_commit", "9f2c1ab")]
    [InlineData("spec_commit", "9F2C1AB0C3D4E5F60718293A4B5C6D7E8F901234")]
    [InlineData("branches", "[a, b, c, d, e, f]")]
    [InlineData("branches", "[-x]")]
    [InlineData("branches", "[a..b]")]
    [InlineData("max_jobs", "0")]
    [InlineData("max_jobs", "201")]
    [InlineData("max_jobs_per_hour", "0")]
    [InlineData("max_jobs_per_hour", "61")]
    [InlineData("max_jobs_per_step", "0")]
    [InlineData("max_jobs_per_step", "21")]
    [InlineData("expires", "tomorrow")]
    [InlineData("expires", "2026-11-22T10:00:01Z")]
    [InlineData("expires", "2026-11-15T10:00:00Z")]
    [InlineData("grants", "[accept, push]")]
    [InlineData("grants", "[accept, accept]")]
    [InlineData("sessions", "yes")]
    public void Parse_FieldOfWrongShape_InvalidFieldNamingIt(string key, string value)
    {
        // Act
        EnvelopeResult result = EnvelopeParser.Parse(Mandate.WithValue(key, value).ToBytes(), Acme);

        // Assert
        result.Rejection!.Reason.Should().Be(EnvelopeRejectionReason.InvalidField);
        result.Rejection.Field.Should().Be(key);
    }

    [Theory]
    [InlineData("2026-11-22T10:00:00Z", true)]
    [InlineData("2026-11-15T10:00:01Z", true)]
    public void Parse_ExpiresWithinSevenDaysOfCreated_Accepted(string expires, bool accepted)
    {
        // Act
        EnvelopeResult result = EnvelopeParser.Parse(Mandate.WithValue("expires", expires).ToBytes(), Acme);

        // Assert
        result.IsAccepted.Should().Be(accepted, result.Rejection?.ToString());
    }

    [Theory]
    [InlineData("spec", "spec_commit")]
    [InlineData("spec_commit", "spec")]
    public void Parse_SpecWithoutItsCommitOrTheReverse_InvalidField(string removed, string left)
    {
        // Act
        EnvelopeResult result = EnvelopeParser.Parse(Mandate.WithoutKey(removed).ToBytes(), Acme);

        // Assert
        result.Rejection!.Reason.Should().Be(EnvelopeRejectionReason.InvalidField);
        result.Rejection.Field.Should().Be(left);
    }

    [Fact]
    public void Parse_OptionalFieldsAbsent_TheDefaults()
    {
        // Arrange
        byte[] bytes = Mandate.WithoutKey("spec").WithoutKey("spec_commit").WithoutKey("branches").WithoutKey("max_jobs_per_hour")
            .WithoutKey("max_jobs_per_step").WithoutKey("grants").WithoutKey("sessions").ToBytes();

        // Act
        MandateFields mandate = EnvelopeParser.Parse(bytes, Acme).Envelope!.Mandate!;

        // Assert
        mandate.Should().BeEquivalentTo(new MandateFields("calizr", "Build the booking pool as the approved spec says", ["Read", "Edit", "Bash(dotnet test *)"], 12, T.AddDays(3)));
        (mandate.MaxJobsPerHour, mandate.MaxJobsPerStep, mandate.Sessions).Should().Be((6, 4, false));
        mandate.Grants.Should().Equal(MandateGrant.Accept);
        mandate.Branches.Should().BeEmpty();
    }

    [Fact]
    public void Parse_MandateWithABody_InvalidFieldBody()
    {
        // Act
        EnvelopeResult result = EnvelopeParser.Parse(Mandate.WithBody("run anything\n"u8.ToArray()).ToBytes(), Acme);

        // Assert
        result.Rejection!.Field.Should().Be("body");
    }

    [Theory]
    [InlineData("job-mandate", "mandate", "not-an-id")]
    [InlineData("job-mandate", "step", "Slice_2")]
    [InlineData("job-mandate", "base", "-force")]
    [InlineData("report-mandate", "mandate", "not-an-id")]
    [InlineData("report-mandate", "step", "Slice_2")]
    public void Parse_JobOrReportMandateFieldOfWrongShape_InvalidFieldNamingIt(string golden, string key, string value)
    {
        // Act
        EnvelopeResult result = EnvelopeParser.Parse(EnvelopeText.From(Golden.Md(golden)).WithValue(key, value).ToBytes(), Acme);

        // Assert
        result.Rejection!.Reason.Should().Be(EnvelopeRejectionReason.InvalidField);
        result.Rejection.Field.Should().Be(key);
    }

    [Theory]
    [InlineData(200, true)]
    [InlineData(201, false)]
    public void Parse_GoalLength_AcceptedUpTo200Characters(int characters, bool accepted)
    {
        // Arrange: ordinary words, so no secret-shaped run
        string goal = string.Join(' ', Enumerable.Repeat("build the pool", 20))[..characters];

        // Act
        EnvelopeResult result = EnvelopeParser.Parse(Mandate.WithValue("goal", goal.TrimEnd() + new string('x', characters - goal.TrimEnd().Length)).ToBytes(), Acme);

        // Assert
        result.IsAccepted.Should().Be(accepted, result.Rejection?.ToString());
    }

    [Theory]
    [InlineData("session", true)]
    [InlineData("run", true)]
    [InlineData("push", false)]
    public void Parse_JobDeliver_RunOrSessionOnly(string value, bool accepted)
    {
        // Act
        EnvelopeResult result = EnvelopeParser.Parse(EnvelopeText.From(Golden.Md("job-mandate")).WithKey("deliver", value).ToBytes(), Acme);

        // Assert
        result.IsAccepted.Should().Be(accepted, result.Rejection?.ToString());
        (accepted || result.Rejection!.Field == "deliver").Should().BeTrue();
    }

    [Fact]
    public async Task CreateMandate_Fields_ReadBackThroughTheParserRules_SignatureEqualsTheGolden()
    {
        // Arrange
        var header = new EnvelopeHeader(Acme, MandateId, MachineName.Central, HomeLaptop, T);
        var signer = new EnvelopeSigner(await TestKeys.StoreWithBothAsync(TestContext.Current.CancellationToken));

        // Act
        Model mandate = Model.CreateMandate(header, Fields);
        EnvelopeResult signed = await signer.SignAsync(mandate, TestKeys.Acme1, TestContext.Current.CancellationToken);

        // Assert: the factory writes the same canonical bytes as the hand-made file
        mandate.Mandate.Should().BeEquivalentTo(Fields with { Grants = [MandateGrant.Accept] });
        signed.Envelope!.Signature.Should().Be("hmac-sha256:" + Golden.Sig("mandate"));
    }

    [Fact]
    public void CreateMandate_FromALaptop_Throws()
    {
        // Arrange
        var header = new EnvelopeHeader(Acme, MandateId, HomeLaptop, HomeLaptop, T);

        // Act
        Action act = () => Model.CreateMandate(header, Fields);

        // Assert
        act.Should().Throw<ArgumentException>().WithParameterName("from");
    }

    [Fact]
    public void CreateJob_WithMandateFields_WrittenAndReadBack_DeliverWrittenOnlyForASession()
    {
        // Arrange
        var header = new EnvelopeHeader(Acme, EnvelopeId.New(T), MachineName.Central, HomeLaptop, T);
        var fields = new JobFields("calizr", Worktree: true, Mandate: MandateId, Step: "slice 2", Base: "feature/12-booking-pool");

        // Act
        Model run = Model.CreateJob(header, fields, "x\n"u8.ToArray());
        Model session = Model.CreateJob(header, fields with { Deliver = JobDelivery.Session }, "x\n"u8.ToArray());

        // Assert
        (run.Job!.Mandate, run.Job.Step, run.Job.Base, run.Job.Deliver).Should().Be((MandateId, "slice 2", "feature/12-booking-pool", JobDelivery.Run));
        run.FrontMatter.Entries.Should().NotContainKey("deliver");
        session.Job!.Deliver.Should().Be(JobDelivery.Session);
    }

    [Fact]
    public void CreateJob_WithoutMandateFields_CanonicalBytesEqualTheExistingJobGolden()
    {
        // Arrange: the job golden of deliverable 03, rebuilt from its typed fields
        Model golden = EnvelopeParser.Parse(Golden.Md("job"), Acme).Envelope!;

        // Act
        Model rebuilt = Model.CreateJob(golden.Header, golden.Job!, golden.Body);

        // Assert
        string canonical = System.Text.Encoding.UTF8.GetString(EnvelopeSigner.Canonicalize(rebuilt));
        canonical.Should().NotContain("mandate").And.NotContain("deliver").And.NotContain("base:").And.NotContain("step:");
    }

    [Theory]
    [InlineData(MandateGrant.Accept, "accept")]
    [InlineData(MandateGrant.Merge, "merge")]
    [InlineData(MandateGrant.Release, "release")]
    [InlineData(MandateGrant.Deploy, "deploy")]
    public void MandateGrant_EveryMember_WireAndBack(MandateGrant member, string wire)
    {
        // Act
        bool known = EnvelopeWire.TryFromWire(wire, out MandateGrant back);

        // Assert
        EnvelopeWire.ToWire(member).Should().Be(wire);
        (known, back).Should().Be((true, member));
        Enum.GetValues<MandateGrant>().Should().HaveCount(4);
    }

    [Theory]
    [InlineData(JobDelivery.Run, "run")]
    [InlineData(JobDelivery.Session, "session")]
    public void JobDelivery_EveryMember_WireAndBack(JobDelivery member, string wire)
    {
        // Act
        bool known = EnvelopeWire.TryFromWire(wire, out JobDelivery back);

        // Assert
        EnvelopeWire.ToWire(member).Should().Be(wire);
        (known, back).Should().Be((true, member));
        Enum.GetValues<JobDelivery>().Should().HaveCount(2);
    }

    [Fact]
    public void EnvelopeType_Mandate_AppendedAfterDecision()
    {
        // Assert
        ((int)EnvelopeType.Mandate).Should().Be((int)EnvelopeType.Decision + 1);
        EnvelopeWire.ToWire(EnvelopeType.Mandate).Should().Be("mandate");
    }
}
