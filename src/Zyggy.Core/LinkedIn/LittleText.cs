using System.Text;

namespace Zyggy.Core.LinkedIn;

/// <summary>
/// LinkedIn's <c>little</c> text format for a post's <c>commentary</c> (spec 36 AC-14, F11): every reserved character
/// <c>| { } @ [ ] ( ) &lt; &gt; \ * _ ~</c> gets a backslash, and <c>#</c> too unless it starts a hashtag (followed by a letter or digit);
/// <c>\n</c> and every other character are kept. So the published post reads exactly as the approved text.
/// </summary>
internal static class LittleText
{
    private const string Reserved = "|{}@[]()<>\\*_~";

    public static string Escape(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length + 16);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var hashtag = c == '#' && i + 1 < text.Length && Rune.TryGetRuneAt(text, i + 1, out var next) && Rune.IsLetterOrDigit(next);
            if (Reserved.Contains(c, StringComparison.Ordinal) || (c == '#' && !hashtag))
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>The inverse of <see cref="Escape"/>: a backslash keeps the character after it (tests only).</summary>
    public static string Unescape(string commentary)
    {
        ArgumentNullException.ThrowIfNull(commentary);
        var builder = new StringBuilder(commentary.Length);
        for (var i = 0; i < commentary.Length; i++)
        {
            if (commentary[i] == '\\' && i + 1 < commentary.Length)
            {
                i++;
            }

            builder.Append(commentary[i]);
        }

        return builder.ToString();
    }
}
