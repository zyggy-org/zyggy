namespace Zyggy.Core.Envelope;

/// <summary>
/// Writes envelopes in canonical style (founding spec §4): <c>---\n</c>, the front matter with <c>sig</c> in its sorted
/// position, <c>---\n</c>, then the body bytes unchanged. Shares the one emitter with
/// <see cref="EnvelopeSigner.Canonicalize(Envelope)"/>, so a written file minus its first line and its <c>sig:</c> line is
/// its canonical form.
/// </summary>
public static class EnvelopeWriter
{
    /// <summary>Serialises a signed envelope into the bytes of its bus file.</summary>
    /// <param name="envelope">The signed envelope.</param>
    /// <returns>The file bytes, UTF-8 without BOM.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="envelope"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when <paramref name="envelope"/> is unsigned; an unsigned envelope is never written.</exception>
    public static byte[] Serialize(Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Signature is null)
        {
            throw new InvalidOperationException("The envelope is unsigned; sign it before writing (§4).");
        }

        byte[] frontMatter = FrontMatterEmitter.Emit(envelope.FrontMatter, includeSig: true);
        return [.. "---\n"u8, .. frontMatter, .. "---\n"u8, .. envelope.Body.Span];
    }
}
