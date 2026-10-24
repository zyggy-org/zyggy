using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;

namespace Zyggy.Core.Tests.Bus;

/// <summary>Plan 08 Step 8: <c>verify</c>'s vocabulary — the first ten outcomes are the §4 rejection reasons' wire names (one mapping, never a second table), the rest are verify's own.</summary>
public sealed class AuditWireTests
{
    [Theory]
    [InlineData(AuditOutcome.Ok, "ok")]
    [InlineData(AuditOutcome.Malformed, "malformed")]
    [InlineData(AuditOutcome.TenantMismatch, "tenant_mismatch")]
    [InlineData(AuditOutcome.SchemaUnsupported, "schema_unsupported")]
    [InlineData(AuditOutcome.KeyIdTenantMismatch, "key_id_tenant_mismatch")]
    [InlineData(AuditOutcome.MissingField, "missing_field")]
    [InlineData(AuditOutcome.InvalidField, "invalid_field")]
    [InlineData(AuditOutcome.MissingSignature, "missing_signature")]
    [InlineData(AuditOutcome.UnknownKey, "unknown_key")]
    [InlineData(AuditOutcome.InvalidSignature, "invalid_signature")]
    [InlineData(AuditOutcome.IdDiffers, "id_differs")]
    [InlineData(AuditOutcome.TypeFolderMismatch, "type_folder_mismatch")]
    [InlineData(AuditOutcome.WriterMismatch, "writer_mismatch")]
    [InlineData(AuditOutcome.UnexpectedPath, "unexpected_path")]
    [InlineData(AuditOutcome.Skipped, "skipped")]
    public void ToWire_EveryMember_ExactString(AuditOutcome outcome, string wire)
    {
        AuditWire.ToWire(outcome).Should().Be(wire);
    }

    [Theory]
    [InlineData(EnvelopeRejectionReason.Malformed, AuditOutcome.Malformed)]
    [InlineData(EnvelopeRejectionReason.TenantMismatch, AuditOutcome.TenantMismatch)]
    [InlineData(EnvelopeRejectionReason.SchemaUnsupported, AuditOutcome.SchemaUnsupported)]
    [InlineData(EnvelopeRejectionReason.KeyIdTenantMismatch, AuditOutcome.KeyIdTenantMismatch)]
    [InlineData(EnvelopeRejectionReason.MissingField, AuditOutcome.MissingField)]
    [InlineData(EnvelopeRejectionReason.InvalidField, AuditOutcome.InvalidField)]
    [InlineData(EnvelopeRejectionReason.MissingSignature, AuditOutcome.MissingSignature)]
    [InlineData(EnvelopeRejectionReason.UnknownKey, AuditOutcome.UnknownKey)]
    [InlineData(EnvelopeRejectionReason.InvalidSignature, AuditOutcome.InvalidSignature)]
    public void FromRejection_TwinMembers_SameWireName(EnvelopeRejectionReason reason, AuditOutcome outcome)
    {
        AuditWire.FromRejection(reason).Should().Be(outcome);
        AuditWire.ToWire(outcome).Should().Be(EnvelopeWire.ToWire(reason));
    }

    [Fact]
    public void Enum_HasFifteenMembers()
    {
        Enum.GetValues<AuditOutcome>().Should().HaveCount(15);
    }
}
