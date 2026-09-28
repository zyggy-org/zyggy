namespace Zyggy.Core.Envelope;

/// <summary>The fields of a <c>context</c> envelope (founding spec §4 "Mandatory fields per type", row <c>context</c>).</summary>
/// <param name="Scope">The <c>scope</c> (mandatory).</param>
public sealed record ContextFields(ContextScope Scope);
