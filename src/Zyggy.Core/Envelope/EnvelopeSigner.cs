using System.Security.Cryptography;

using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Envelope;

/// <summary>
/// Produces the canonical signing bytes of an envelope and signs and verifies envelopes with HMAC-SHA256 under the
/// tenant's key read through <see cref="ISecretStore"/> (founding spec §4 "Signature").
/// </summary>
public sealed class EnvelopeSigner
{
    /// <summary>The minimum HMAC key length in bytes (RFC 2104: not shorter than the hash output). Not configurable.</summary>
    public const int MinimumKeyLength = 32;

    private const string KeyIdField = "key_id";

    private readonly ISecretStore _secrets;

    /// <summary>Creates a signer over a secret store.</summary>
    /// <param name="secrets">The store holding the tenants' HMAC keys under <c>hmac/&lt;n&gt;</c>.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="secrets"/> is null.</exception>
    public EnvelopeSigner(ISecretStore secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        _secrets = secrets;
    }

    /// <summary>
    /// Returns the canonical form of <paramref name="envelope"/>: the front matter re-emitted without the top-level
    /// <c>sig</c> key (§4 "Canonical form, normative details"), every line ending in <c>\n</c>, then <c>---\n</c>, then the
    /// body bytes unchanged.
    /// </summary>
    /// <remarks>The single source of truth for the signing input (§9); frozen by the golden-file tests under <c>tests/golden/</c>.</remarks>
    /// <param name="envelope">The envelope.</param>
    /// <returns>The bytes that are signed.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="envelope"/> is null.</exception>
    public static byte[] Canonicalize(Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        byte[] frontMatter = FrontMatterEmitter.Emit(envelope.FrontMatter, includeSig: false);
        return [.. frontMatter, .. "---\n"u8, .. envelope.Body.Span];
    }

    /// <summary>
    /// Signs <paramref name="envelope"/> with the key <paramref name="signWith"/>: sets <c>key_id</c>, replacing any existing
    /// value and signature, and <c>sig: hmac-sha256:&lt;64 lowercase hex&gt;</c> over the canonical form.
    /// </summary>
    /// <param name="envelope">The envelope; it is not modified.</param>
    /// <param name="signWith">The key id; its tenant must be the envelope's tenant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The signed copy, or <see cref="EnvelopeRejectionReason.UnknownKey"/> when the key is absent or too short.</returns>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="signWith"/> belongs to another tenant.</exception>
    public async Task<EnvelopeResult> SignAsync(Envelope envelope, KeyId signWith, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(signWith);
        if (signWith.Tenant != envelope.Header.Tenant)
        {
            throw new ArgumentException("The signing key belongs to another tenant than the envelope.", nameof(signWith));
        }

        (ReadOnlyMemory<byte> key, EnvelopeRejection? rejection) = await ReadKeyAsync(envelope, signWith, cancellationToken).ConfigureAwait(false);
        if (rejection is not null)
        {
            return EnvelopeResult.Rejected(rejection);
        }

        Envelope candidate = envelope.WithSignature(signWith, signature: null);
        byte[] digest = HMACSHA256.HashData(key.Span, Canonicalize(candidate));
        string signature = EnvelopeParser.SignaturePrefix + Convert.ToHexStringLower(digest);
        return EnvelopeResult.Accepted(candidate.WithSignature(signWith, signature));
    }

    /// <summary>
    /// Verifies <paramref name="envelope"/>: tenant first (a foreign tenant's key is never looked up), then the signature
    /// against the key named by <c>key_id</c> over the canonical form (§4 "Signature").
    /// </summary>
    /// <remarks>
    /// Rotation is whatever the store holds: any <c>&lt;tenant&gt;/&lt;n&gt;</c> present is accepted. Store exceptions
    /// (I/O, corrupt file) propagate: they are infrastructure faults, not protocol outcomes.
    /// </remarks>
    /// <param name="envelope">The parsed envelope.</param>
    /// <param name="expectedTenant">The receiver's tenant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The envelope when the signature matches, otherwise the rejection.</returns>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    public async Task<EnvelopeResult> VerifyAsync(Envelope envelope, TenantId expectedTenant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(expectedTenant);
        if (envelope.Header.Tenant != expectedTenant)
        {
            return EnvelopeResult.Rejected(EnvelopeRejectionReason.TenantMismatch, "tenant", "tenant differs from the receiver's tenant");
        }

        if (envelope.Signature is null || envelope.KeyId is null)
        {
            return EnvelopeResult.Rejected(EnvelopeRejectionReason.MissingSignature, "sig", "sig is missing");
        }

        (ReadOnlyMemory<byte> key, EnvelopeRejection? rejection) = await ReadKeyAsync(envelope, envelope.KeyId, cancellationToken).ConfigureAwait(false);
        if (rejection is not null)
        {
            return EnvelopeResult.Rejected(rejection);
        }

        byte[] expected = Convert.FromHexString(envelope.Signature.AsSpan(EnvelopeParser.SignaturePrefix.Length));
        byte[] actual = HMACSHA256.HashData(key.Span, Canonicalize(envelope));
        return CryptographicOperations.FixedTimeEquals(actual, expected)
            ? EnvelopeResult.Accepted(envelope)
            : EnvelopeResult.Rejected(EnvelopeRejectionReason.InvalidSignature, "sig", "signature does not match the canonical form");
    }

    /// <summary>Parses an envelope file, then verifies it; a parse rejection is returned without touching the store.</summary>
    /// <param name="file">The file bytes.</param>
    /// <param name="expectedTenant">The receiver's tenant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The verified envelope, or the first rejection.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="expectedTenant"/> is null.</exception>
    public async Task<EnvelopeResult> ParseAndVerifyAsync(ReadOnlyMemory<byte> file, TenantId expectedTenant, CancellationToken cancellationToken)
    {
        EnvelopeResult parsed = EnvelopeParser.Parse(file, expectedTenant);
        return parsed.IsAccepted
            ? await VerifyAsync(parsed.Envelope, expectedTenant, cancellationToken).ConfigureAwait(false)
            : parsed;
    }

    private async Task<(ReadOnlyMemory<byte> Key, EnvelopeRejection? Rejection)> ReadKeyAsync(
        Envelope envelope, KeyId keyId, CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte>? key = await _secrets.GetAsync(envelope.Header.Tenant, keyId.SecretName, cancellationToken).ConfigureAwait(false);
        return key switch
        {
            null => (default, new EnvelopeRejection(EnvelopeRejectionReason.UnknownKey, KeyIdField, "signing key is absent from the secret store")),
            { Length: < MinimumKeyLength } => (default, new EnvelopeRejection(EnvelopeRejectionReason.UnknownKey, KeyIdField, "signing key is shorter than 32 bytes")),
            _ => (key.Value, null),
        };
    }
}
