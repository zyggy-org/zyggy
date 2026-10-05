using System.Text;

namespace Zyggy.Core.Verbs;

/// <summary>Text helpers that reproduce how the template scripts quote user input in their messages.</summary>
internal static class ShellText
{
    /// <summary>
    /// <c>${s:0:n}</c> under the scripts' <c>LC_ALL=C</c>: the first <paramref name="bytes"/> bytes of the UTF-8 text, kept to whole
    /// characters (the shell may cut inside a character; a message never needs that).
    /// </summary>
    public static string Prefix(string text, int bytes)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = new StringBuilder();
        var used = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            used += rune.Utf8SequenceLength;
            if (used > bytes)
            {
                break;
            }

            result.Append(rune.ToString());
        }

        return result.ToString();
    }
}
