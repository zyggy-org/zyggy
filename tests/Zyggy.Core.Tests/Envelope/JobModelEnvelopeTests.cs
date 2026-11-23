using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Envelope;

/// <summary>
/// Spec 50 slice 1: the optional job fields <c>model</c> and <c>job_type</c> and the optional report field <c>job_type</c> — typed
/// parse of the hand-derived goldens <c>job-model</c> and <c>report-model</c>, the factory signing to their exact bytes (AC-1, AC-6),
/// and the parse-time refusals with <c>invalid_field</c> (AC-1).
/// </summary>
public sealed class JobModelEnvelopeTests
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    /// <summary>The golden job (no model), the base for every shape case.</summary>
    private static EnvelopeText Job => EnvelopeText.From(Golden.Md("job"));

    // AC-1: the golden reads back both fields.
    [Fact]
    public void Parse_GoldenJobModel_YieldsModelAndJobType()
    {
        // Act
        EnvelopeResult result = EnvelopeParser.Parse(Golden.Md("job-model"), Acme);

        // Assert
        result.IsAccepted.Should().BeTrue(result.Rejection?.ToString());
        JobFields job = result.Envelope!.Job!;
        new { job.Model, job.JobType }.Should().BeEquivalentTo(new { Model = "claude-opus-5-5", JobType = "implementation" });
    }

    // AC-1: a job without the fields reads both as absent (the existing golden, unchanged).
    [Fact]
    public void Parse_GoldenJobWithoutModel_BothNull()
    {
        // Act
        JobFields job = EnvelopeParser.Parse(Golden.Md("job"), Acme).Envelope!.Job!;

        // Assert
        new { job.Model, job.JobType }.Should().BeEquivalentTo(new { Model = (string?)null, JobType = (string?)null });
    }

    // AC-1: the factory writes both fields, and the signed bytes equal the hand-derived golden.
    [Fact]
    public async Task CreateJob_WithModelAndJobType_SignsToTheGoldenBytes()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var header = new EnvelopeHeader(
            Acme, EnvelopeId.Parse("01M84NJ0AKCGQ6MQJBZXKEW9Q0"), MachineName.Parse("central"), MachineName.Parse("home-laptop"), new DateTimeOffset(2026, 11, 23, 9, 0, 0, TimeSpan.Zero));
        var fields = new JobFields("shop", Worktree: true, Model: "claude-opus-5-5", JobType: "implementation");
        var signer = new EnvelopeSigner(await TestKeys.StoreWithBothAsync(ct));

        // Act
        Model created = Model.CreateJob(header, fields, "Build slice 1 of the cart feature.\n"u8.ToArray());
        EnvelopeResult signed = await signer.SignAsync(created, TestKeys.Acme1, ct);

        // Assert
        EnvelopeWriter.Serialize(signed.Envelope!).Should().Equal(Golden.Md("job-model"));
    }

    // AC-1: full ids of the current family, a one-letter id, a 64-character id and a context-size suffix are accepted.
    [Theory]
    [InlineData("claude-opus-5-5")]
    [InlineData("claude-fable-5-2")]
    [InlineData("claude-haiku-5-5")]
    [InlineData("claude-sonnet-4.5")]
    [InlineData("'claude-sonnet-5-5[1m]'")]
    [InlineData("x")]
    public void Parse_ModelOfValidShape_IsAccepted(string yamlValue)
    {
        // Act
        EnvelopeResult result = EnvelopeParser.Parse(Job.WithKey("model", yamlValue).ToBytes(), Acme);

        // Assert
        result.IsAccepted.Should().BeTrue(result.Rejection?.ToString());
    }

    [Fact]
    public void Parse_ModelOfSixtyFourCharacters_IsAccepted()
    {
        // Act
        EnvelopeResult result = EnvelopeParser.Parse(Job.WithKey("model", "claude-" + new string('a', 57)).ToBytes(), Acme);

        // Assert
        result.IsAccepted.Should().BeTrue(result.Rejection?.ToString());
    }

    public static TheoryData<string, string> BadModels => new()
    {
        { "alias opus", "opus" },
        { "alias sonnet", "sonnet" },
        { "alias haiku", "haiku" },
        { "alias fable", "fable" },
        { "alias default", "default" },
        { "alias best", "best" },
        { "alias opusplan", "opusplan" },
        { "alias with a suffix", "'opus[1m]'" },
        { "an upper-case letter", "Claude-opus-5-5" },
        { "a leading digit", "5-opus" },
        { "a leading dash (a flag)", "'-p'" },
        { "a leading dot", "'.claude'" },
        { "a space", "'claude opus'" },
        { "an underscore", "claude_opus" },
        { "a slash", "claude/opus" },
        { "65 characters", "claude-" + new string('a', 58) },
        { "an empty suffix", "'claude-opus-5-5[]'" },
        { "two suffixes", "'claude-opus-5-5[1m][2m]'" },
        { "a suffix with a dash", "'claude-opus-5-5[1-m]'" },
        { "text after the suffix", "'claude-opus-5-5[1m]x'" },
        { "a sequence", "[claude-opus-5-5]" },
    };

    // AC-1: one case per rule of the model shape, aliases included.
    [Theory]
    [MemberData(nameof(BadModels))]
    public void Parse_ModelBreakingTheShapeRule_InvalidFieldModel(string because, string yamlValue)
    {
        // Act
        EnvelopeRejection rejection = Reject(Job.WithKey("model", yamlValue).ToBytes());

        // Assert
        new { rejection.Reason, rejection.Field }.Should().BeEquivalentTo(new { Reason = EnvelopeRejectionReason.InvalidField, Field = "model" }, because);
    }

    public static TheoryData<string, string> BadJobTypes => new()
    {
        { "an upper-case letter", "Spec" },
        { "an underscore", "spec_writing" },
        { "a space", "'spec writing'" },
        { "64 characters", new string('a', 64) },
        { "a sequence", "[spec]" },
    };

    // AC-1: the job_type label shape, on a job and on a report.
    [Theory]
    [MemberData(nameof(BadJobTypes))]
    public void Parse_JobTypeBreakingTheLabelRule_InvalidFieldJobType(string because, string yamlValue)
    {
        // Act
        EnvelopeRejection job = Reject(Job.WithKey("job_type", yamlValue).ToBytes());
        EnvelopeRejection report = Reject(EnvelopeText.From(Golden.Md("report")).WithKey("job_type", yamlValue).ToBytes());

        // Assert
        new { Job = job.Field, Report = report.Field, job.Reason }
            .Should().BeEquivalentTo(new { Job = "job_type", Report = "job_type", Reason = EnvelopeRejectionReason.InvalidField }, because);
    }

    [Theory]
    [InlineData("spec")]
    [InlineData("implementation")]
    [InlineData("build")]
    [InlineData("1")]
    [InlineData("code-review")]
    public void Parse_JobTypeOfValidShape_IsAccepted(string value)
    {
        // Act
        EnvelopeResult result = EnvelopeParser.Parse(Job.WithKey("job_type", value).ToBytes(), Acme);

        // Assert
        result.Envelope?.Job?.JobType.Should().Be(value, result.Rejection?.ToString());
    }

    // AC-1: a fetch or command job runs no model, so it may not carry one.
    [Theory]
    [InlineData("fetch")]
    [InlineData("command")]
    public void Parse_ModelFreeKindWithModel_InvalidFieldModel(string kind)
    {
        // Arrange
        EnvelopeText job = kind == "fetch"
            ? Job.WithKey("kind", "fetch").WithKey("fetch_path", "a.txt")
            : Job.WithKey("kind", "command").WithKey("command", "build");

        // Act
        EnvelopeRejection rejection = Reject(job.WithKey("model", "claude-opus-5-5").ToBytes());

        // Assert
        new { rejection.Reason, rejection.Field }.Should().BeEquivalentTo(new { Reason = EnvelopeRejectionReason.InvalidField, Field = "model" });
    }

    // AC-1: the factory refuses what the parser refuses, so no sender can sign such a job.
    [Theory]
    [InlineData("opus", null, "model")]
    [InlineData("--fallback-model", null, "model")]
    [InlineData(null, "Spec", "job_type")]
    public void CreateJob_FieldBreakingTheShapeRule_ThrowsArgumentExceptionNamingIt(string? model, string? jobType, string field)
    {
        // Arrange
        var header = new EnvelopeHeader(Acme, EnvelopeId.Parse("01M84NJ0AKCGQ6MQJBZXKEW9Q0"), MachineName.Parse("central"), MachineName.Parse("home-laptop"), DateTimeOffset.UnixEpoch);

        // Act
        Action act = () => Model.CreateJob(header, new JobFields("shop", Model: model, JobType: jobType), "x\n"u8.ToArray());

        // Assert
        act.Should().Throw<ArgumentException>().Which.ParamName.Should().Be(field);
    }

    // AC-6: the report golden reads back the model that ran and the job type.
    [Fact]
    public void Parse_GoldenReportModel_YieldsModelAndJobType()
    {
        // Act
        EnvelopeResult result = EnvelopeParser.Parse(Golden.Md("report-model"), Acme);

        // Assert
        result.IsAccepted.Should().BeTrue(result.Rejection?.ToString());
        ReportFields report = result.Envelope!.Report!;
        new { report.Model, report.JobType, report.Status }.Should().BeEquivalentTo(new { Model = "claude-opus-5-5", JobType = "implementation", Status = ReportStatus.Done });
    }

    // AC-6: the factory writes job_type, and the signed bytes equal the hand-derived golden.
    [Fact]
    public async Task CreateReport_WithJobType_SignsToTheGoldenBytes()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var header = new EnvelopeHeader(
            Acme,
            EnvelopeId.Parse("01M84NJ0AKCGQ6MQJBZXKEW9Q1"),
            MachineName.Parse("home-laptop"),
            MachineName.Parse("central"),
            new DateTimeOffset(2026, 11, 23, 10, 5, 0, TimeSpan.Zero),
            EnvelopePriority.Normal,
            EnvelopeId.Parse("01M84NJ0AKCGQ6MQJBZXKEW9Q0"));
        var fields = new ReportFields(
            ReportStatus.Done,
            Started: new DateTimeOffset(2026, 11, 23, 9, 40, 0, TimeSpan.Zero),
            Finished: new DateTimeOffset(2026, 11, 23, 10, 4, 30, TimeSpan.Zero),
            Model: "claude-opus-5-5",
            JobType: "implementation");
        var signer = new EnvelopeSigner(await TestKeys.StoreWithBothAsync(ct));

        // Act
        Model created = Model.CreateReport(header, fields, "## REPORT\nsummary: Slice 1 of the cart feature is built.\n"u8.ToArray());
        EnvelopeResult signed = await signer.SignAsync(created, TestKeys.Acme1, ct);

        // Assert
        EnvelopeWriter.Serialize(signed.Envelope!).Should().Equal(Golden.Md("report-model"));
    }

    private static EnvelopeRejection Reject(byte[] file)
    {
        EnvelopeResult result = EnvelopeParser.Parse(file, Acme);
        result.IsAccepted.Should().BeFalse();
        return result.Rejection!;
    }
}
