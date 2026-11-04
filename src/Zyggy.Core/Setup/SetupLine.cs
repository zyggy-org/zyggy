using Zyggy.Core.Envelope;

namespace Zyggy.Core.Setup;

/// <summary>One output line of <c>zyggy setup</c> (spec 42 Contracts): the item, its outcome and a detail that never holds a secret value.</summary>
/// <param name="Item">The item.</param>
/// <param name="Outcome">The outcome.</param>
/// <param name="Detail">Paths, fingerprints, a remedy or a reason; never a secret value. <see langword="null"/> or empty prints no detail.</param>
public sealed record SetupLine(SetupItem Item, SetupOutcome Outcome, string? Detail)
{
    /// <summary>Renders the line: <c>setup: &lt;item words&gt;: &lt;outcome words&gt;[ - &lt;detail&gt;]</c> (plan 42 A1).</summary>
    /// <param name="signWith">The signing key the <see cref="SetupItem.SecretSigningKey"/> words name.</param>
    /// <returns>The line without its line ending.</returns>
    public string Render(KeyId? signWith)
    {
        string head = "setup: " + SetupWire.ItemWords(Item, signWith) + ": " + SetupWire.OutcomeWords(Outcome);
        return string.IsNullOrEmpty(Detail) ? head : head + " - " + Detail;
    }
}
