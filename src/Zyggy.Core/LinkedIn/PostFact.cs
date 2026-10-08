using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using Zyggy.Core.Memory;

namespace Zyggy.Core.LinkedIn;

/// <summary>
/// The one <c>[observed]</c> fact a published post leaves in memory (spec 36 AC-22):
/// <c>- [observed] &lt;date&gt; (linkedin &lt;urn&gt;): Posted on LinkedIn (&lt;visibility&gt;): "&lt;excerpt&gt;"</c> in
/// <c>inbox/linkedin-&lt;date&gt;.md</c>, through the shared writer the dream reads. The excerpt is the first sentence or line, URLs as
/// <c>[link]</c>, at most 120 characters (spec 36 Assumption 4).
/// </summary>
internal static partial class PostFact
{
    public const int ExcerptMax = 120;

    /// <summary>The fact part (what the validator judges), without the tag, date and source.</summary>
    public static string Fact(PostVisibility visibility, string text) => $"Posted on LinkedIn ({visibility.Wire()}): \"{Excerpt(text)}\"";

    public static string Line(DateOnly date, string urn, PostVisibility visibility, string text) =>
        $"- [observed] {date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} (linkedin {urn}): {Fact(visibility, text)}";

    /// <summary>The first sentence (up to <c>.</c>, <c>!</c> or <c>?</c> before whitespace or the end) or line, URLs as <c>[link]</c>, <c>"</c> as <c>'</c>, cut to 120.</summary>
    public static string Excerpt(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var end = text.Length;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                end = i;
                break;
            }

            if (text[i] is '.' or '!' or '?' && (i + 1 == text.Length || char.IsWhiteSpace(text[i + 1])))
            {
                end = i + 1;
                break;
            }
        }

        var excerpt = Url().Replace(text[..end], "[link]").Replace('"', '\'').Trim();
        var runes = excerpt.EnumerateRunes().ToList();
        return runes.Count <= ExcerptMax
            ? excerpt
            : new StringBuilder().AppendJoin(string.Empty, runes.Take(ExcerptMax - 1)).Append('…').ToString();
    }

    /// <summary>Appends the line to <c>inbox/linkedin-&lt;date&gt;.md</c> (a new file gets the fixed front matter).</summary>
    public static void Write(MemoryPaths memory, DateOnly date, string line)
    {
        ArgumentNullException.ThrowIfNull(memory);
        var day = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        FactLineWriter.Append(
            memory.InboxFile($"linkedin-{day}.md"), $"linkedin {day}", $"posts published on LinkedIn on {day} (linkedin publish_post)", date, [line]);
    }

    // FactValidator's URL shapes, to the next whitespace.
    [GeneratedRegex(@"(?:[A-Za-z][A-Za-z0-9+.-]*://|www\.)\S*", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Url();
}
