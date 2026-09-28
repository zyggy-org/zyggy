using System.Diagnostics.CodeAnalysis;

namespace Zyggy.Core.Envelope;

/// <summary>The outcome of parsing, verifying or signing an envelope: the envelope, or the reason it was refused.</summary>
public sealed class EnvelopeResult
{
    private EnvelopeResult(Envelope? envelope, EnvelopeRejection? rejection)
    {
        Envelope = envelope;
        Rejection = rejection;
    }

    /// <summary>Gets the envelope when accepted.</summary>
    public Envelope? Envelope { get; }

    /// <summary>Gets the rejection when refused.</summary>
    public EnvelopeRejection? Rejection { get; }

    /// <summary>Gets a value indicating whether the envelope was accepted.</summary>
    [MemberNotNullWhen(true, nameof(Envelope))]
    [MemberNotNullWhen(false, nameof(Rejection))]
    public bool IsAccepted => Envelope is not null;

    internal static EnvelopeResult Accepted(Envelope envelope) => new(envelope, null);

    internal static EnvelopeResult Rejected(EnvelopeRejection rejection) => new(null, rejection);

    internal static EnvelopeResult Rejected(EnvelopeRejectionReason reason, string? field, string detail) =>
        new(null, new EnvelopeRejection(reason, field, detail));
}
