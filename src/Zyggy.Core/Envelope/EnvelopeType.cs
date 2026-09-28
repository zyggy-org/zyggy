namespace Zyggy.Core.Envelope;

/// <summary>The envelope <c>type</c> field (founding spec §4); wire strings via <see cref="EnvelopeWire"/>.</summary>
public enum EnvelopeType
{
    /// <summary><c>job</c>: work for the addressed node.</summary>
    Job,

    /// <summary><c>report</c>: the outcome of a job.</summary>
    Report,

    /// <summary><c>context</c>: facts sent to Central's memory.</summary>
    Context,
}
