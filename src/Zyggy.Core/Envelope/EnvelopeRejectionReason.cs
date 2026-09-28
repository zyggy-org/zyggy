namespace Zyggy.Core.Envelope;

/// <summary>
/// Why an envelope was refused (founding spec §4 "Signature", "Mandatory fields per type"). Closed: adding a member is a
/// protocol change. Wire strings via <see cref="EnvelopeWire"/>.
/// </summary>
public enum EnvelopeRejectionReason
{
    /// <summary><c>malformed</c>: not an envelope (delimiters, UTF-8, YAML subset, root mapping, duplicate key).</summary>
    Malformed,

    /// <summary><c>tenant_mismatch</c>: <c>tenant</c> differs from the receiver's tenant.</summary>
    TenantMismatch,

    /// <summary><c>schema_unsupported</c>: <c>schema</c> is a higher major version than this node supports.</summary>
    SchemaUnsupported,

    /// <summary><c>key_id_tenant_mismatch</c>: the tenant part of <c>key_id</c> differs from <c>tenant</c>.</summary>
    KeyIdTenantMismatch,

    /// <summary><c>missing_field</c>: a mandatory field is absent or null.</summary>
    MissingField,

    /// <summary><c>invalid_field</c>: a field has the wrong shape.</summary>
    InvalidField,

    /// <summary><c>missing_signature</c>: <c>sig</c> is absent or null.</summary>
    MissingSignature,

    /// <summary><c>unknown_key</c>: the key named by <c>key_id</c> is absent from the store or too short.</summary>
    UnknownKey,

    /// <summary><c>invalid_signature</c>: <c>sig</c> is malformed or does not match the canonical form.</summary>
    InvalidSignature,
}
