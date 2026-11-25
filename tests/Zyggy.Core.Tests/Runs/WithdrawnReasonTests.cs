using Zyggy.Core.Envelope;
using Zyggy.Core.Runs;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Runs;

/// <summary>
/// Spec 61 slice 4, AC-13: the reason <c>withdrawn</c> has its wire string and is accepted in a report. A 0.30.0 machine reads it:
/// its report reading rule (a reason is any snake_case token, <c>EnvelopeParser</c> unchanged since <c>v0.30.0</c>) accepts the
/// token, so a 0.30.0 reader shows the report as <c>failed</c> / <c>withdrawn</c> without knowing the member.
/// </summary>
public sealed class WithdrawnReasonTests
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    // AC-13: the wire string, round trip included.
    [Fact]
    public void ToWire_Withdrawn_IsWithdrawnAndRoundTrips()
    {
        // Act
        string wire = RunFailureReasonWire.ToWire(RunFailureReason.Withdrawn);
        bool ok = RunFailureReasonWire.TryFromWire("withdrawn", out RunFailureReason back);

        // Assert
        (wire, ok, back).Should().Be(("withdrawn", true, RunFailureReason.Withdrawn));
    }

    // AC-13: appended last, so every earlier member keeps its value.
    [Fact]
    public void Withdrawn_AppendedLast()
    {
        // Assert
        ((int)RunFailureReason.Withdrawn).Should().Be((int)RunFailureReason.ModelUnavailable + 1);
    }

    // AC-13: the factory writes a failed / withdrawn report, and it verifies after signing.
    [Fact]
    public async Task CreateReport_FailedWithdrawn_SignsAndVerifies()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var signer = new EnvelopeSigner(await TestKeys.StoreWithBothAsync(ct));
        var header = new EnvelopeHeader(
            Acme, EnvelopeId.Parse("01M87B0000RRRRRRRRRRRRRRRR"), MachineName.Central, MachineName.Central,
            new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.Zero), InReplyTo: EnvelopeId.Parse("01J8Y3N7Q2X9Z4A5B6C7D8E9F0"));
        Model report = Model.CreateReport(header, new ReportFields(ReportStatus.Failed, RunFailureReasonWire.ToWire(RunFailureReason.Withdrawn)), "withdrawn before it started\n"u8.ToArray());
        byte[] bytes = EnvelopeWriter.Serialize((await signer.SignAsync(report, TestKeys.Acme1, ct)).Envelope!);

        // Act
        EnvelopeResult verified = await signer.ParseAndVerifyAsync(bytes, Acme, ct);

        // Assert
        (verified.Envelope?.Report?.Status, verified.Envelope?.Report?.Reason).Should().Be((ReportStatus.Failed, "withdrawn"));
    }

    // AC-13, compatibility: the 0.30.0 reading rule (any snake_case token) accepts `reason: withdrawn` on a golden report.
    [Fact]
    public void Parse_ReportWithReasonWithdrawn_AcceptedByTheSnakeCaseTokenRule()
    {
        // Arrange
        byte[] report = EnvelopeText.From(Golden.Md("report")).WithValue("reason", "withdrawn").ToBytes();

        // Act
        EnvelopeResult result = EnvelopeParser.Parse(report, Acme);

        // Assert
        result.Envelope?.Report?.Reason.Should().Be("withdrawn", result.Rejection?.ToString());
    }
}
