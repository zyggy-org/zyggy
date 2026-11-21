using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Envelope;

/// <summary>
/// Spec 59 slice 6 (AC-9): the job fields <c>max_turns</c> and <c>continue</c> — written by the factory, read back by the parser, and
/// their parse-time refusals (<c>continue</c> only with <c>worktree: true</c>, both only on a model job).
/// </summary>
public sealed class ContinueEnvelopeTests
{
    private const string Earlier = "01J8Y3N7Q2X9Z4A5B6C7D8E9F1";

    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly DateTimeOffset T = new(2026, 11, 21, 9, 0, 0, TimeSpan.Zero);

    private static readonly MachineName Central = MachineName.Parse("central");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    // AC-9: a continuing job with its own turn limit signs, serialises and reads back.
    [Fact]
    public async Task CreateJob_ContinueAndMaxTurns_ReadBack()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var fields = new JobFields("shop", Worktree: true, MaxTurns: 120, Continue: EnvelopeId.Parse(Earlier));
        var signer = new EnvelopeSigner(await TestKeys.StoreWithBothAsync(ct));

        // Act
        EnvelopeResult signed = await signer.SignAsync(Model.CreateJob(JobHeader(), fields, "Go on.\n"u8.ToArray()), TestKeys.Acme1, ct);
        EnvelopeResult read = await signer.ParseAndVerifyAsync(EnvelopeWriter.Serialize(signed.Envelope!), Acme, ct);

        // Assert
        read.Envelope!.Job.Should().BeEquivalentTo(fields);
    }

    // A plain job keeps its bytes: the two keys are written only when set.
    [Fact]
    public void CreateJob_NeitherSet_WritesNoKeys()
    {
        // Act
        Model created = Model.CreateJob(JobHeader(), new JobFields("shop"), "Investigate.\n"u8.ToArray());

        // Assert
        created.FrontMatter.Entries.Keys.Should().NotContain(["max_turns", "continue"]);
    }

    [Theory]
    [InlineData("continue", Earlier, "false", "continue")]
    [InlineData("continue", "not-an-id", "true", "continue")]
    [InlineData("max_turns", "0", "true", "max_turns")]
    [InlineData("max_turns", "ten", "true", "max_turns")]
    public void Parse_ContinueOrMaxTurnsOutOfPlace_Refused(string key, string value, string worktree, string field)
    {
        // Arrange
        EnvelopeText job = EnvelopeText.From(Golden.Md("job")).WithoutKey("worktree").WithKey("worktree", worktree).WithKey(key, value);

        // Act
        EnvelopeResult result = EnvelopeParser.Parse(job.ToBytes(), Acme);

        // Assert
        new { result.Rejection!.Reason, result.Rejection.Field }.Should().BeEquivalentTo(new { Reason = EnvelopeRejectionReason.InvalidField, Field = field });
    }

    [Theory]
    [InlineData("continue", Earlier)]
    [InlineData("max_turns", "20")]
    public void Parse_OnAFetchJob_Refused(string key, string value)
    {
        // Arrange
        EnvelopeText job = EnvelopeText.From(Golden.Md("job")).WithKey("kind", "fetch").WithKey("fetch_path", "a.txt").WithKey(key, value);

        // Act
        EnvelopeResult result = EnvelopeParser.Parse(job.ToBytes(), Acme);

        // Assert
        result.Rejection!.Field.Should().Be(key);
    }

    [Fact]
    public void Parse_ContinueWithWorktree_Accepted()
    {
        // Arrange
        EnvelopeText job = EnvelopeText.From(Golden.Md("job")).WithKey("continue", Earlier).WithKey("max_turns", "1");

        // Act
        EnvelopeResult result = EnvelopeParser.Parse(job.ToBytes(), Acme);

        // Assert
        new { result.Envelope!.Job!.Continue, result.Envelope.Job.MaxTurns }.Should().BeEquivalentTo(new { Continue = EnvelopeId.Parse(Earlier), MaxTurns = 1 });
    }

    private static EnvelopeHeader JobHeader() => new(Acme, EnvelopeId.Parse("01J8Y3N7Q2X9Z4A5B6C7D8E9F2"), Central, HomeLaptop, T);
}
