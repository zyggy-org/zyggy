using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Bus;

/// <summary>
/// An item Central settled in a pickup pass (spec 41, plan 41 A10): its <c>items/</c> file to remove, Central's signed report on it to
/// add to <c>nodes/central/reports/</c>, and the commit-body outcome (<c>filed archive/&lt;project&gt;/&lt;slug&gt;.&lt;ext&gt;</c> or
/// <c>refused &lt;word&gt;</c>).
/// </summary>
/// <param name="Item">The item's file in its sender's <c>items/</c> folder.</param>
/// <param name="SignedReport">Central's signed report, <c>in_reply_to</c> the item.</param>
/// <param name="Outcome">The outcome written after the arrow in the commit body.</param>
public sealed record SettledItem(BusFile Item, Model SignedReport, string Outcome);
