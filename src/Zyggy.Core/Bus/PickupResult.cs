using Zyggy.Core.Envelope;

namespace Zyggy.Core.Bus;

/// <summary>What one pickup pass on Central did (spec 13).</summary>
/// <param name="Inboxed">Files whose inbox file was created in this pass.</param>
/// <param name="ArchivedOnly">Files archived without a new inbox file: a <c>rejected</c> report, a body with nothing left to copy, or an inbox file already present.</param>
/// <param name="LinesDropped">Secret-shaped context lines dropped plus report lines refused.</param>
/// <param name="Left">Files left where they are: refused by a check, or their inbox write failed.</param>
/// <param name="Archive">The archive write; <see langword="null"/> when nothing was due.</param>
public sealed record PickupResult(int Inboxed, int ArchivedOnly, int LinesDropped, int Left, WriteOutcome? Archive);

/// <summary>Why the pickup left a file where it is (spec 13 AC-7/AC-8); the wire names reuse §4 and the audit's.</summary>
public enum PickupLeftReason
{
    /// <summary><c>tenant_mismatch</c>.</summary>
    TenantMismatch,

    /// <summary><c>malformed</c>.</summary>
    Malformed,

    /// <summary><c>schema_unsupported</c>.</summary>
    SchemaUnsupported,

    /// <summary><c>key_id_tenant_mismatch</c>.</summary>
    KeyIdTenantMismatch,

    /// <summary><c>missing_field</c>.</summary>
    MissingField,

    /// <summary><c>invalid_field</c>.</summary>
    InvalidField,

    /// <summary><c>missing_signature</c>.</summary>
    MissingSignature,

    /// <summary><c>unknown_key</c>.</summary>
    UnknownKey,

    /// <summary><c>invalid_signature</c>.</summary>
    InvalidSignature,

    /// <summary><c>writer_mismatch</c>: <c>from</c> is not the folder's machine.</summary>
    WriterMismatch,

    /// <summary><c>type_folder_mismatch</c>: the envelope type does not match its folder.</summary>
    TypeFolderMismatch,

    /// <summary><c>id_differs</c>: the file name is not the envelope id.</summary>
    IdDiffers,

    /// <summary><c>inbox_write_failed</c>.</summary>
    InboxWriteFailed,
}

/// <summary>The wire names of <see cref="PickupLeftReason"/>.</summary>
internal static class PickupLeftWire
{
    public static string ToWire(PickupLeftReason reason) => reason switch
    {
        PickupLeftReason.TenantMismatch => EnvelopeWire.ToWire(EnvelopeRejectionReason.TenantMismatch),
        PickupLeftReason.Malformed => EnvelopeWire.ToWire(EnvelopeRejectionReason.Malformed),
        PickupLeftReason.SchemaUnsupported => EnvelopeWire.ToWire(EnvelopeRejectionReason.SchemaUnsupported),
        PickupLeftReason.KeyIdTenantMismatch => EnvelopeWire.ToWire(EnvelopeRejectionReason.KeyIdTenantMismatch),
        PickupLeftReason.MissingField => EnvelopeWire.ToWire(EnvelopeRejectionReason.MissingField),
        PickupLeftReason.InvalidField => EnvelopeWire.ToWire(EnvelopeRejectionReason.InvalidField),
        PickupLeftReason.MissingSignature => EnvelopeWire.ToWire(EnvelopeRejectionReason.MissingSignature),
        PickupLeftReason.UnknownKey => EnvelopeWire.ToWire(EnvelopeRejectionReason.UnknownKey),
        PickupLeftReason.InvalidSignature => EnvelopeWire.ToWire(EnvelopeRejectionReason.InvalidSignature),
        PickupLeftReason.WriterMismatch => AuditWire.ToWire(AuditOutcome.WriterMismatch),
        PickupLeftReason.TypeFolderMismatch => AuditWire.ToWire(AuditOutcome.TypeFolderMismatch),
        PickupLeftReason.IdDiffers => AuditWire.ToWire(AuditOutcome.IdDiffers),
        PickupLeftReason.InboxWriteFailed => "inbox_write_failed",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "not a pickup reason"),
    };

    public static PickupLeftReason FromRejection(EnvelopeRejectionReason reason) => reason switch
    {
        EnvelopeRejectionReason.Malformed => PickupLeftReason.Malformed,
        EnvelopeRejectionReason.TenantMismatch => PickupLeftReason.TenantMismatch,
        EnvelopeRejectionReason.SchemaUnsupported => PickupLeftReason.SchemaUnsupported,
        EnvelopeRejectionReason.KeyIdTenantMismatch => PickupLeftReason.KeyIdTenantMismatch,
        EnvelopeRejectionReason.MissingField => PickupLeftReason.MissingField,
        EnvelopeRejectionReason.InvalidField => PickupLeftReason.InvalidField,
        EnvelopeRejectionReason.MissingSignature => PickupLeftReason.MissingSignature,
        EnvelopeRejectionReason.UnknownKey => PickupLeftReason.UnknownKey,
        EnvelopeRejectionReason.InvalidSignature => PickupLeftReason.InvalidSignature,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "verification never yields this reason"),
    };
}
