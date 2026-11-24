using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Envelope;

/// <summary>
/// Spec 61 slice 4, AC-12: the <c>withdrawal</c> envelope — the hand-derived golden parses to its fields, its canonical bytes and
/// signature equal the files and verify, the factory signs the same fields to its exact bytes, a missing <c>in_reply_to</c> is
/// refused, the body is empty or one line of 1–200 characters, and a withdrawal under another tenant's key never verifies.
/// </summary>
public sealed class WithdrawalEnvelopeTests
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly EnvelopeId JobId = EnvelopeId.Parse("01J8Y3N7Q2X9Z4A5B6C7D8E9F0");

    private static EnvelopeText Withdrawal => EnvelopeText.From(Golden.Md("withdrawal"));

    private static EnvelopeHeader Header => new(
        Acme, EnvelopeId.Parse("01M87B0000WWWWWWWWWWWWWWWW"), MachineName.Central, MachineName.Parse("home-laptop"),
        new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.Zero), InReplyTo: JobId);

    // AC-12: the golden reads back as a withdrawal of the job case, from Central to the machine that claimed it.
    [Fact]
    public void Parse_WithdrawalGolden_TypedFields()
    {
        // Act
        EnvelopeResult result = EnvelopeParser.Parse(Golden.Md("withdrawal"), Acme);

        // Assert
        result.IsAccepted.Should().BeTrue(result.Rejection?.ToString());
        Model withdrawal = result.Envelope!;
        new { withdrawal.Type, withdrawal.Header.From, withdrawal.Header.To, withdrawal.Header.InReplyTo, withdrawal.BodyText }.Should().BeEquivalentTo(new
        {
            Type = EnvelopeType.Withdrawal,
            From = MachineName.Central,
            To = MachineName.Parse("home-laptop"),
            InReplyTo = JobId,
            BodyText = "no longer needed: the staging fix landed in another job\n",
        });
    }

    // AC-12: canonical bytes and signature are the hand-derived files, and the file verifies under acme/1.
    [Fact]
    public async Task WithdrawalGolden_CanonicalBytesAndSignatureEqualTheFiles_AndVerify()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var signer = new EnvelopeSigner(await TestKeys.StoreWithBothAsync(ct));
        Model envelope = EnvelopeParser.Parse(Golden.Md("withdrawal"), Acme).Envelope!;

        // Act
        byte[] canonical = EnvelopeSigner.Canonicalize(envelope);
        EnvelopeResult signed = await signer.SignAsync(envelope, TestKeys.Acme1, ct);
        EnvelopeResult verified = await signer.ParseAndVerifyAsync(Golden.Md("withdrawal"), Acme, ct);

        // Assert
        canonical.Should().Equal(Golden.Canonical("withdrawal"));
        signed.Envelope!.Signature.Should().Be("hmac-sha256:" + Golden.Sig("withdrawal"));
        verified.IsAccepted.Should().BeTrue(verified.Rejection?.ToString());
    }

    // AC-12: the factory signs the same fields to the golden's exact bytes.
    [Fact]
    public async Task CreateWithdrawal_SameFields_SignsToTheGoldenBytes()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var signer = new EnvelopeSigner(await TestKeys.StoreWithBothAsync(ct));

        // Act
        Model created = Model.CreateWithdrawal(Header, "no longer needed: the staging fix landed in another job");
        EnvelopeResult signed = await signer.SignAsync(created, TestKeys.Acme1, ct);

        // Assert
        EnvelopeWriter.Serialize(signed.Envelope!).Should().Equal(Golden.Md("withdrawal"));
    }

    // Behavior §1: without a --why line the body is empty.
    [Fact]
    public void CreateWithdrawal_NoWhy_EmptyBody()
    {
        // Act
        Model created = Model.CreateWithdrawal(Header, why: null);

        // Assert
        (created.Type, created.Body.Length, created.Header.InReplyTo).Should().Be((EnvelopeType.Withdrawal, 0, JobId));
    }

    // AC-12: a missing in_reply_to is refused, naming the field.
    [Fact]
    public void Parse_WithdrawalWithoutInReplyTo_MissingField()
    {
        // Act
        EnvelopeResult result = EnvelopeParser.Parse(Withdrawal.WithoutKey("in_reply_to").ToBytes(), Acme);

        // Assert
        (result.Rejection!.Reason, result.Rejection.Field).Should().Be((EnvelopeRejectionReason.MissingField, "in_reply_to"));
    }

    // AC-12: the factory refuses a header without the job id too.
    [Fact]
    public void CreateWithdrawal_NoInReplyTo_Throws()
    {
        // Act
        Action act = () => Model.CreateWithdrawal(Header with { InReplyTo = null }, why: null);

        // Assert
        act.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("in_reply_to");
    }

    // Behavior §1: the body is empty or one --why line of 1–200 characters (data, never instructions).
    [Theory]
    [InlineData("first line\nsecond line\n")]
    [InlineData("\n")]
    public void Parse_WithdrawalBodyNotOneLine_InvalidBody(string body)
    {
        // Act
        EnvelopeResult result = EnvelopeParser.Parse(Withdrawal.WithBody(System.Text.Encoding.UTF8.GetBytes(body)).ToBytes(), Acme);

        // Assert
        (result.Rejection!.Reason, result.Rejection.Field).Should().Be((EnvelopeRejectionReason.InvalidField, "body"));
    }

    // Behavior §1: 200 characters is the longest --why line.
    [Theory]
    [InlineData(200, true)]
    [InlineData(201, false)]
    public void Parse_WithdrawalBodyLength_AcceptedUpTo200(int length, bool accepted)
    {
        // Act
        EnvelopeResult result = EnvelopeParser.Parse(Withdrawal.WithBody(System.Text.Encoding.UTF8.GetBytes(new string('w', length) + "\n")).ToBytes(), Acme);

        // Assert
        result.IsAccepted.Should().Be(accepted);
    }

    // AC-12 / task: a withdrawal under another tenant's key is refused before any signature is checked.
    [Fact]
    public async Task ParseAndVerify_WithdrawalUnderAnotherTenantsKey_Refused()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var signer = new EnvelopeSigner(await TestKeys.StoreWithBothAsync(ct));
        byte[] forged = Withdrawal.WithValue("key_id", "globex/1").ToBytes();

        // Act
        EnvelopeResult result = await signer.ParseAndVerifyAsync(forged, Acme, ct);

        // Assert
        result.Rejection!.Reason.Should().Be(EnvelopeRejectionReason.KeyIdTenantMismatch);
    }

    // AC-12 / task: a withdrawal of another tenant is refused by this tenant's reader.
    [Fact]
    public async Task ParseAndVerify_WithdrawalOfAnotherTenant_Refused()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var signer = new EnvelopeSigner(await TestKeys.StoreWithBothAsync(ct));
        byte[] foreign = Withdrawal.WithValue("tenant", "globex").WithValue("key_id", "globex/1").ToBytes();

        // Act
        EnvelopeResult result = await signer.ParseAndVerifyAsync(foreign, Acme, ct);

        // Assert
        result.Rejection!.Reason.Should().Be(EnvelopeRejectionReason.TenantMismatch);
    }

    // AC-12: one field changed after signing is caught by the signature (signed like a job).
    [Fact]
    public async Task ParseAndVerify_WithdrawalRetargetedAfterSigning_InvalidSignature()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var signer = new EnvelopeSigner(await TestKeys.StoreWithBothAsync(ct));
        byte[] tampered = Withdrawal.WithValue("in_reply_to", "01M87B0000JJJJJJJJJJJJJJJJ").ToBytes();

        // Act
        EnvelopeResult result = await signer.ParseAndVerifyAsync(tampered, Acme, ct);

        // Assert
        result.Rejection!.Reason.Should().Be(EnvelopeRejectionReason.InvalidSignature);
    }

    // AC-12: the wire string, appended last so every earlier member keeps its value.
    [Fact]
    public void EnvelopeType_Withdrawal_WireIsWithdrawalAndAppendedLast()
    {
        // Act
        string wire = EnvelopeWire.ToWire(EnvelopeType.Withdrawal);

        // Assert
        (wire, (int)EnvelopeType.Withdrawal).Should().Be(("withdrawal", (int)EnvelopeType.Mandate + 1));
    }
}
