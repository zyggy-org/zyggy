using System.Text;

namespace Zyggy.Core.Memory;

/// <summary>The template's one-line rules (<c>zy_collapse_line</c>, <c>zy_char_count</c> in <c>lib.sh</c>), in .NET.</summary>
internal static class TextCollapse
{
    /// <summary>
    /// <c>tr '\r\n\t' '   ' | tr -s ' ' | sed 's/^ //; s/ $//'</c>: CR, LF and TAB become a space, runs of spaces become one,
    /// one leading and one trailing space are removed. No other character is touched.
    /// </summary>
    public static string Line(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            var mapped = c is '\r' or '\n' or '\t' ? ' ' : c;
            if (mapped == ' ' && builder.Length > 0 && builder[^1] == ' ')
            {
                continue;
            }

            builder.Append(mapped);
        }

        if (builder.Length > 0 && builder[0] == ' ')
        {
            builder.Remove(0, 1);
        }

        if (builder.Length > 0 && builder[^1] == ' ')
        {
            builder.Remove(builder.Length - 1, 1);
        }

        return builder.ToString();
    }

    /// <summary><c>wc -m</c> under a UTF-8 locale: the number of Unicode scalar values.</summary>
    public static int CharCount(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var count = 0;
        foreach (var _ in text.EnumerateRunes())
        {
            count++;
        }

        return count;
    }
}
