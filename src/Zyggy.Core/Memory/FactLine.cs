namespace Zyggy.Core.Memory;

/// <summary>
/// The one fact-line grammar of the memory inbox and the context envelopes (spec 12 Step 1):
/// <c>- [&lt;tag&gt;] &lt;date&gt;&lt;hint&gt;[ [&lt;source&gt;]]: &lt;fact&gt;</c>. <c>remember</c> and <c>zyggy context</c> write it byte for byte alike,
/// and the dream's ingest copies it verbatim.
/// </summary>
internal static class FactLine
{
    /// <summary>Formats one line; the provenance bracket appears only when <paramref name="source"/> is not empty.</summary>
    public static string Format(string tag, string hint, string source, string date, string fact)
    {
        ArgumentNullException.ThrowIfNull(source);
        var provenance = source.Length == 0 ? string.Empty : $" [{source}]";
        return $"- [{tag}] {date}{hint}{provenance}: {fact}";
    }
}
