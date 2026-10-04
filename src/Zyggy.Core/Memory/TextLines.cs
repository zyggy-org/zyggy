namespace Zyggy.Core.Memory;

/// <summary>Splits text into lines the way <c>awk</c> does: on <c>\n</c>, a final newline adds no empty line, CR is kept.</summary>
internal static class TextLines
{
    public static List<string> Split(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }

        var lines = text.Split('\n').ToList();
        if (text[^1] == '\n')
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }
}
