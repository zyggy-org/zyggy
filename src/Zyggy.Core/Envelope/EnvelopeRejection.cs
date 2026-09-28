namespace Zyggy.Core.Envelope;

/// <summary>Why an envelope was refused, and which field is at fault.</summary>
/// <param name="Reason">The closed reason.</param>
/// <param name="Field">The front-matter key at fault, or <see langword="null"/> when no single key is.</param>
/// <param name="Detail">A short constant sentence; it never contains a key, a digest or a field value.</param>
public sealed record EnvelopeRejection(EnvelopeRejectionReason Reason, string? Field, string Detail);
