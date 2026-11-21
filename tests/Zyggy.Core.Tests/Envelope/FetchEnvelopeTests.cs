using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Envelope;

/// <summary>
/// Spec 59 slice 1: the job fields <c>kind</c>, <c>fetch_path</c> and <c>fetch_ref</c>, and the report fields <c>file_name</c> and
/// <c>file_sha256</c> — written by the factories, read back by the parser, and their parse-time refusals.
/// </summary>
public sealed class FetchEnvelopeTests
{
    private const string Sha = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly DateTimeOffset T = new(2026, 11, 21, 9, 0, 0, TimeSpan.Zero);

    private static readonly MachineName Central = MachineName.Parse("central");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    // AC-1: a fetch job signs, serialises and reads back with its three fields.
    [Fact]
    public async Task CreateJob_FetchKind_ReadsBackKindPathAndRef()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var fields = new JobFields("shop", Kind: JobKind.Fetch, FetchPath: "docs/plan.md", FetchRef: "feature/12-cart");
        var signer = new EnvelopeSigner(await TestKeys.StoreWithBothAsync(ct));

        // Act
        EnvelopeResult signed = await signer.SignAsync(Model.CreateJob(JobHeader(), fields, "fetch docs/plan.md\n"u8.ToArray()), TestKeys.Acme1, ct);
        EnvelopeResult read = await signer.ParseAndVerifyAsync(EnvelopeWriter.Serialize(signed.Envelope!), Acme, ct);

        // Assert
        read.Envelope!.Job.Should().BeEquivalentTo(fields);
    }

    // A model job keeps its bytes: kind is written only when it is not the default.
    [Fact]
    public void CreateJob_ModelKind_WritesNoKindKey()
    {
        // Act
        Model created = Model.CreateJob(JobHeader(), new JobFields("shop"), "Investigate.\n"u8.ToArray());

        // Assert
        created.FrontMatter.Entries.Keys.Should().NotContain(["kind", "fetch_path", "fetch_ref"]);
    }

    [Theory]
    [InlineData("kind", "fetch", "fetch_path")]
    [InlineData("kind", "shell", "kind")]
    [InlineData("fetch_path", "docs/plan.md", "fetch_path")]
    [InlineData("fetch_ref", "main", "fetch_ref")]
    public void Parse_FetchFieldsOutOfPlace_Refused(string key, string value, string field)
    {
        // Arrange
        EnvelopeText job = EnvelopeText.From(Golden.Md("job")).WithKey(key, value);

        // Act
        EnvelopeResult result = EnvelopeParser.Parse(job.ToBytes(), Acme);

        // Assert
        result.Rejection!.Field.Should().Be(field);
    }

    [Fact]
    public void Parse_FetchRefNotABranchName_RefusedInvalidField()
    {
        // Arrange
        EnvelopeText job = EnvelopeText.From(Golden.Md("job")).WithKey("kind", "fetch").WithKey("fetch_path", "a.txt").WithKey("fetch_ref", "a..b");

        // Act
        EnvelopeResult result = EnvelopeParser.Parse(job.ToBytes(), Acme);

        // Assert
        new { result.Rejection!.Reason, result.Rejection.Field }.Should().BeEquivalentTo(new { Reason = EnvelopeRejectionReason.InvalidField, Field = "fetch_ref" });
    }

    // AC-1 / Q2: file_name and file_sha256 sit in the signed front matter of a report and read back.
    [Fact]
    public async Task CreateReport_WithAttachedFile_ReadsBackNameAndSha()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var fields = new ReportFields(ReportStatus.Done, FileName: "plan.md", FileSha256: Sha);
        var signer = new EnvelopeSigner(await TestKeys.StoreWithBothAsync(ct));

        // Act
        EnvelopeResult signed = await signer.SignAsync(Model.CreateReport(ReportHeader(), fields, "fetched\n"u8.ToArray()), TestKeys.Acme1, ct);
        EnvelopeResult read = await signer.ParseAndVerifyAsync(EnvelopeWriter.Serialize(signed.Envelope!), Acme, ct);

        // Assert
        new { read.Envelope!.Report!.FileName, read.Envelope.Report.FileSha256 }.Should().BeEquivalentTo(new { FileName = "plan.md", FileSha256 = Sha });
    }

    [Theory]
    [InlineData("docs/plan.md", Sha, "file_name")]
    [InlineData("..", Sha, "file_name")]
    [InlineData("plan.md", "ABC", "file_sha256")]
    [InlineData("plan.md", null, "file_sha256")]
    [InlineData(null, Sha, "file_name")]
    public void CreateReport_BadAttachedFileFields_Refused(string? name, string? sha, string field)
    {
        // Act
        Action act = () => Model.CreateReport(ReportHeader(), new ReportFields(ReportStatus.Done, FileName: name, FileSha256: sha), "x\n"u8.ToArray());

        // Assert
        act.Should().Throw<ArgumentException>().Which.ParamName.Should().Be(field);
    }

    private static EnvelopeHeader JobHeader() => new(Acme, EnvelopeId.Parse("01J8Y3N7Q2X9Z4A5B6C7D8E9F2"), Central, HomeLaptop, T);

    private static EnvelopeHeader ReportHeader() =>
        new(Acme, EnvelopeId.Parse("01J8Y3N7Q2X9Z4A5B6C7D8E9F3"), HomeLaptop, Central, T, EnvelopePriority.Normal, EnvelopeId.Parse("01J8Y3N7Q2X9Z4A5B6C7D8E9F2"));
}
