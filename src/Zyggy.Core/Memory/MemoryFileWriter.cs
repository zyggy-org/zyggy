using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Zyggy.Core.Memory;

/// <summary>
/// Writes a memory file atomically: LF line endings, UTF-8 without BOM, a final newline; the whole file goes to
/// <c>&lt;file&gt;.zyggy-tmp-&lt;ulid&gt;</c> first and is then moved over the target.
/// </summary>
internal static partial class MemoryFileWriter
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static void Write(string path, MemoryFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        WriteAtomically(path, Render(file));
    }

    public static string Render(MemoryFile file)
    {
        var text = new StringBuilder();
        if (file.HasFrontMatter)
        {
            text.Append("---\n");
            if (file.Name is not null)
            {
                text.Append("name: ").Append(Scalar(file.Name)).Append('\n');
            }

            if (file.Description is not null)
            {
                text.Append("description: ").Append(Scalar(file.Description)).Append('\n');
            }

            if (file.Aliases.Count > 0)
            {
                text.Append("aliases: [").Append(string.Join(", ", file.Aliases.Select(Scalar))).Append("]\n");
            }

            if (file.Updated is { } updated)
            {
                text.Append("updated: ").Append(updated.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('\n');
            }

            foreach (var (key, value) in file.UnknownKeys)
            {
                text.Append(key).Append(": ").Append(Scalar(value)).Append('\n');
            }

            text.Append("---\n");
        }

        foreach (var line in file.BodyLines)
        {
            text.Append(line).Append('\n');
        }

        return text.ToString();
    }

    public static void WriteAtomically(string path, string text)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new ArgumentException("The path has no directory.", nameof(path));
        Directory.CreateDirectory(directory);
        var temp = path + ".zyggy-tmp-" + Ulid.NewUlid();
        try
        {
            File.WriteAllBytes(temp, Utf8NoBom.GetBytes(text));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    // Plain when YAML reads it back as the same string; double-quoted otherwise.
    private static string Scalar(string value) =>
        Plain().IsMatch(value) && !value.Contains(": ", StringComparison.Ordinal) && !value.Contains(" #", StringComparison.Ordinal)
            ? value
            : "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    [GeneratedRegex(@"^[A-Za-z0-9(][^\[\]{},""'\n\r\t]*[^\s:]$|^[A-Za-z0-9]$", RegexOptions.CultureInvariant)]
    private static partial Regex Plain();
}
