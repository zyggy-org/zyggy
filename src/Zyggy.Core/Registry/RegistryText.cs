using System.Globalization;
using System.Text;

namespace Zyggy.Core.Registry;

/// <summary>
/// Cleans harvested text before it reaches a registry file (spec 14, Injection): control characters (Unicode category Cc) and the
/// line and paragraph separators removed, then cut to 150 UTF-16 units without splitting a surrogate pair.
/// </summary>
internal static class RegistryText
{
    private const char LineSeparator = (char)0x2028;
    private const char ParagraphSeparator = (char)0x2029;

    internal static string Clean(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(Math.Min(text.Length, RegistryEntry.MaxDescriptionLength + 1));
        foreach (char c in text)
        {
            if (c is LineSeparator or ParagraphSeparator || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Control)
            {
                continue;
            }

            builder.Append(c);
            if (builder.Length > RegistryEntry.MaxDescriptionLength)
            {
                break;
            }
        }

        if (builder.Length <= RegistryEntry.MaxDescriptionLength)
        {
            return builder.ToString();
        }

        int cut = RegistryEntry.MaxDescriptionLength;
        if (char.IsHighSurrogate(builder[cut - 1]))
        {
            cut--;
        }

        return builder.ToString(0, cut);
    }
}
