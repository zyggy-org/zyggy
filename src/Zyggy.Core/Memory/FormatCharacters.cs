using System.Globalization;
using System.Text;

namespace Zyggy.Core.Memory;

/// <summary>
/// The one normalisation step the work mail filter and the private-detail strip take before any matching (spec 58 AC-9, D-Q18):
/// every Unicode format character (category Cf — soft hyphen U+00AD, zero-width space and joiners U+200B–U+200D, direction marks and
/// embeddings, word joiner U+2060, BOM U+FEFF, tag characters, …) is removed, so an invisible character can neither split a term or
/// a number nor hide it.
/// </summary>
internal static class FormatCharacters
{
    /// <summary>Returns <paramref name="text"/> without its format characters.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The text without them; the same instance when it holds none.</returns>
    public static string Remove(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!text.EnumerateRunes().Any(r => Rune.GetUnicodeCategory(r) == UnicodeCategory.Format))
        {
            return text;
        }

        var result = new StringBuilder(text.Length);
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) != UnicodeCategory.Format)
            {
                result.Append(rune.ToString());
            }
        }

        return result.ToString();
    }
}
