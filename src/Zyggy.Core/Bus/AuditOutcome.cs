using Zyggy.Core.Envelope;

namespace Zyggy.Core.Bus;

/// <summary>
/// What <c>zyggy verify</c> says about one file (spec 08 AC-4, ex-Q2, A8-2): the first ten are the §4 rejection reasons (one mapping,
/// never a second table), the rest are verify's own checks. Not a §4 change: <see cref="EnvelopeRejectionReason"/> and <c>PROTOCOL.md</c> are untouched.
/// </summary>
public enum AuditOutcome
{
    /// <summary>Parsed, verified, consistent with its folder.</summary>
    Ok,

    /// <summary>§4: not a front-matter document.</summary>
    Malformed,

    /// <summary>§4: the <c>tenant</c> field is not the path's tenant.</summary>
    TenantMismatch,

    /// <summary>§4: a <c>schema</c> this build does not read.</summary>
    SchemaUnsupported,

    /// <summary>§4: the <c>key_id</c>'s tenant is not the envelope's.</summary>
    KeyIdTenantMismatch,

    /// <summary>§4: a mandatory field is absent.</summary>
    MissingField,

    /// <summary>§4: a field breaks its rule.</summary>
    InvalidField,

    /// <summary>§4: no <c>sig</c>.</summary>
    MissingSignature,

    /// <summary>§4: the <c>key_id</c> names a key this machine does not hold (after a rotation: the retired key — ex-Q3).</summary>
    UnknownKey,

    /// <summary>§4: the signature does not verify.</summary>
    InvalidSignature,

    /// <summary>The front-matter <c>id</c> is not the file name.</summary>
    IdDiffers,

    /// <summary>The envelope's <c>type</c> does not belong in its folder (a report in <c>jobs/</c>).</summary>
    TypeFolderMismatch,

    /// <summary>The §4 write rule: a report or context whose <c>from</c>, or a job whose <c>to</c>, is not the folder's machine.</summary>
    WriterMismatch,

    /// <summary>A path §4 does not know (a stray folder, a file at the tenant root other than the ones skipped).</summary>
    UnexpectedPath,

    /// <summary>Not judged: a non-envelope file name, the registry file (unsigned in v1), the root <c>policy.yaml</c> until deliverable 18.</summary>
    Skipped,
}

/// <summary>The wire names of <see cref="AuditOutcome"/>: the §4 ones through <see cref="EnvelopeWire"/>, the rest verify's.</summary>
public static class AuditWire
{
    /// <summary>The wire name.</summary>
    /// <param name="outcome">The outcome.</param>
    /// <returns>The lowercase snake-case word.</returns>
    public static string ToWire(AuditOutcome outcome) => outcome switch
    {
        AuditOutcome.Ok => "ok",
        AuditOutcome.Malformed => EnvelopeWire.ToWire(EnvelopeRejectionReason.Malformed),
        AuditOutcome.TenantMismatch => EnvelopeWire.ToWire(EnvelopeRejectionReason.TenantMismatch),
        AuditOutcome.SchemaUnsupported => EnvelopeWire.ToWire(EnvelopeRejectionReason.SchemaUnsupported),
        AuditOutcome.KeyIdTenantMismatch => EnvelopeWire.ToWire(EnvelopeRejectionReason.KeyIdTenantMismatch),
        AuditOutcome.MissingField => EnvelopeWire.ToWire(EnvelopeRejectionReason.MissingField),
        AuditOutcome.InvalidField => EnvelopeWire.ToWire(EnvelopeRejectionReason.InvalidField),
        AuditOutcome.MissingSignature => EnvelopeWire.ToWire(EnvelopeRejectionReason.MissingSignature),
        AuditOutcome.UnknownKey => EnvelopeWire.ToWire(EnvelopeRejectionReason.UnknownKey),
        AuditOutcome.InvalidSignature => EnvelopeWire.ToWire(EnvelopeRejectionReason.InvalidSignature),
        AuditOutcome.IdDiffers => "id_differs",
        AuditOutcome.TypeFolderMismatch => "type_folder_mismatch",
        AuditOutcome.WriterMismatch => "writer_mismatch",
        AuditOutcome.UnexpectedPath => "unexpected_path",
        AuditOutcome.Skipped => "skipped",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "not an audit outcome"),
    };

    /// <summary>The outcome of a §4 rejection, member for member.</summary>
    /// <param name="reason">The parser's or signer's reason.</param>
    /// <returns>The twin outcome.</returns>
    public static AuditOutcome FromRejection(EnvelopeRejectionReason reason) => reason switch
    {
        EnvelopeRejectionReason.Malformed => AuditOutcome.Malformed,
        EnvelopeRejectionReason.TenantMismatch => AuditOutcome.TenantMismatch,
        EnvelopeRejectionReason.SchemaUnsupported => AuditOutcome.SchemaUnsupported,
        EnvelopeRejectionReason.KeyIdTenantMismatch => AuditOutcome.KeyIdTenantMismatch,
        EnvelopeRejectionReason.MissingField => AuditOutcome.MissingField,
        EnvelopeRejectionReason.InvalidField => AuditOutcome.InvalidField,
        EnvelopeRejectionReason.MissingSignature => AuditOutcome.MissingSignature,
        EnvelopeRejectionReason.UnknownKey => AuditOutcome.UnknownKey,
        EnvelopeRejectionReason.InvalidSignature => AuditOutcome.InvalidSignature,
        // The intake's own check (the listener's "to" rule) cannot come out of ParseAndVerifyAsync; mapped for completeness.
        EnvelopeRejectionReason.WrongRecipient => AuditOutcome.WriterMismatch,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "not a rejection reason"),
    };
}
