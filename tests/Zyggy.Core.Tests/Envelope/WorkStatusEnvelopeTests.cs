using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Envelope;

// AC-1 (status part): the `status` type parses, signs and verifies; a missing or misshapen mandatory field is refused by name.
public sealed class WorkStatusEnvelopeTests
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly DateTimeOffset Since = new(2026, 11, 15, 9, 3, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Activity = new(2026, 11, 15, 10, 42, 0, TimeSpan.Zero);

    private static EnvelopeText Status => EnvelopeText.From(Golden.Md("status"));

    [Fact]
    public void Parse_StatusGolden_TypedFields()
    {
        // Act
        EnvelopeResult result = EnvelopeParser.Parse(Golden.Md("status"), Acme);

        // Assert
        result.IsAccepted.Should().BeTrue(result.Rejection?.ToString());
        result.Envelope!.Type.Should().Be(EnvelopeType.Status);
        result.Envelope.Header.To.Should().Be(MachineName.Central);
        result.Envelope.Body.IsEmpty.Should().BeTrue();
        result.Envelope.Status.Should().Be(
            new StatusFields("calizr", "5b3fd3ac", Since, Activity, "feature/12-booking-pool", "9f2c1ab", 3, "_specs/12-booking-pool.md", "slice 2"));
    }

    [Fact]
    public async Task StatusGolden_CanonicalBytesAndSignatureEqualTheFiles()
    {
        // Arrange
        Model status = EnvelopeParser.Parse(Golden.Md("status"), Acme).Envelope!;
        var signer = new EnvelopeSigner(await TestKeys.StoreWithBothAsync(TestContext.Current.CancellationToken));

        // Act
        byte[] canonical = EnvelopeSigner.Canonicalize(status);
        EnvelopeResult signed = await signer.SignAsync(status, TestKeys.Acme1, TestContext.Current.CancellationToken);

        // Assert
        canonical.Should().Equal(Golden.Canonical("status"));
        signed.Envelope!.Signature.Should().Be("hmac-sha256:" + Golden.Sig("status"));
    }

    [Fact]
    public async Task ParseAndVerify_StatusGolden_Accepted()
    {
        // Arrange
        var signer = new EnvelopeSigner(await TestKeys.StoreWithBothAsync(TestContext.Current.CancellationToken));

        // Act
        EnvelopeResult result = await signer.ParseAndVerifyAsync(Golden.Md("status"), Acme, TestContext.Current.CancellationToken);

        // Assert
        result.IsAccepted.Should().BeTrue(result.Rejection?.ToString());
    }

    [Theory]
    [InlineData("project")]
    [InlineData("session")]
    [InlineData("since")]
    [InlineData("activity")]
    public void Parse_StatusMandatoryFieldRemoved_MissingFieldNamingIt(string key)
    {
        // Act
        EnvelopeResult result = EnvelopeParser.Parse(Status.WithoutKey(key).ToBytes(), Acme);

        // Assert
        result.Rejection!.Reason.Should().Be(EnvelopeRejectionReason.MissingField);
        result.Rejection.Field.Should().Be(key);
    }

    [Theory]
    [InlineData("project", "Calizr")]
    [InlineData("project", "'a b'")]
    [InlineData("session", "5b3fd3a")]
    [InlineData("session", "5B3FD3AC")]
    [InlineData("session", "5b3fd3acd")]
    [InlineData("since", "2026-11-15 09:03:00")]
    [InlineData("activity", "yesterday")]
    [InlineData("branch", "\"feature\\tx\"")]
    [InlineData("head", "9F2C1AB")]
    [InlineData("head", "9f2c1a")]
    [InlineData("head", "not-hex")]
    [InlineData("dirty", "-1")]
    [InlineData("dirty", "many")]
    [InlineData("spec", "../outside.md")]
    [InlineData("spec", "/absolute.md")]
    [InlineData("spec", "'C:\\specs\\a.md'")]
    [InlineData("step", "Slice 2")]
    [InlineData("step", "slice_2")]
    [InlineData("to", "work-laptop")]
    public void Parse_StatusFieldOfWrongShape_InvalidFieldNamingIt(string key, string value)
    {
        // Act
        EnvelopeResult result = EnvelopeParser.Parse(Status.WithValue(key, value).ToBytes(), Acme);

        // Assert
        result.Rejection!.Reason.Should().Be(EnvelopeRejectionReason.InvalidField);
        result.Rejection.Field.Should().Be(key);
    }

    [Fact]
    public void Parse_StatusWithBody_InvalidFieldBody()
    {
        // Act
        EnvelopeResult result = EnvelopeParser.Parse(Status.WithBody("working on the pool\n"u8.ToArray()).ToBytes(), Acme);

        // Assert
        result.Rejection!.Reason.Should().Be(EnvelopeRejectionReason.InvalidField);
        result.Rejection.Field.Should().Be("body");
    }

    [Fact]
    public void Parse_StatusWithFixedFieldsOnly_AcceptedWithNullOptionals()
    {
        // Arrange
        byte[] file = Status.WithoutKey("branch").WithoutKey("head").WithoutKey("dirty").WithoutKey("spec").WithoutKey("step").ToBytes();

        // Act
        EnvelopeResult result = EnvelopeParser.Parse(file, Acme);

        // Assert
        result.IsAccepted.Should().BeTrue(result.Rejection?.ToString());
        result.Envelope!.Status.Should().Be(new StatusFields("calizr", "5b3fd3ac", Since, Activity));
    }

    [Fact]
    public void CreateStatus_FixedFields_FrontMatterHoldsOnlyTheFourStatusKeys()
    {
        // Arrange
        var header = new EnvelopeHeader(Acme, EnvelopeId.New(Activity), HomeLaptop, MachineName.Central, Activity);

        // Act
        Model status = Model.CreateStatus(header, new StatusFields("calizr", "5b3fd3ac", Since, Activity));

        // Assert
        status.Type.Should().Be(EnvelopeType.Status);
        status.Body.IsEmpty.Should().BeTrue();
        status.FrontMatter.Entries.Keys.Should().BeEquivalentTo(
            "schema", "id", "tenant", "type", "from", "to", "created", "priority", "project", "session", "since", "activity");
    }

    [Fact]
    public void CreateStatus_ToAnotherMachine_Throws()
    {
        // Arrange
        var header = new EnvelopeHeader(Acme, EnvelopeId.New(Activity), HomeLaptop, MachineName.Parse("work-laptop"), Activity);

        // Act
        Action act = () => Model.CreateStatus(header, new StatusFields("calizr", "5b3fd3ac", Since, Activity));

        // Assert
        act.Should().Throw<ArgumentException>().WithMessage("*'to'*");
    }

    [Fact]
    public void EnvelopeType_Status_WireIsStatus_AppendedLast()
    {
        // Act
        string text = EnvelopeWire.ToWire(EnvelopeType.Status);
        bool back = EnvelopeWire.TryFromWire("status", out EnvelopeType type);

        // Assert
        text.Should().Be("status");
        back.Should().BeTrue();
        type.Should().Be(EnvelopeType.Status);
        ((int)EnvelopeType.Status).Should().Be(4);
    }
}
