using System.Globalization;
using System.Text;

namespace Zyggy.Core.Memory;

/// <summary>
/// Appends fact lines to an inbox file exactly as the template's <c>zy_atomic_append</c> does: an existing file is re-emitted
/// byte for byte (a missing final newline is added, as awk does) except the value of an <c>updated:</c> line inside a front
/// matter that opens on line 1; a new file gets a fixed front matter with no YAML quoting. Written atomically; never git.
/// The dream hashes these lines into its ledger, so the bytes are a contract.
/// </summary>
internal static class FactLineWriter
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static void Append(string path, string frontMatterName, string frontMatterDescription, DateOnly today, IReadOnlyList<string> lines)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(frontMatterName);
        ArgumentNullException.ThrowIfNull(frontMatterDescription);
        ArgumentNullException.ThrowIfNull(lines);

        var date = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        using var output = new MemoryStream();
        if (File.Exists(path))
        {
            ReEmit(File.ReadAllBytes(path), date, output);
        }
        else
        {
            Write(output, $"---\nname: {frontMatterName}\ndescription: {frontMatterDescription}\nupdated: {date}\n---\n");
        }

        foreach (var line in lines)
        {
            Write(output, line + "\n");
        }

        MemoryFileWriter.WriteAtomically(path, output.ToArray());
    }

    // awk 'NR == 1 && $0 == "---" { fm = 1; print; next } fm && $0 == "---" { fm = 0 } fm && /^updated:/ { print "updated: " today; next } { print }'
    private static void ReEmit(byte[] bytes, string date, MemoryStream output)
    {
        var frontMatter = false;
        var start = 0;
        var number = 0;
        while (start < bytes.Length)
        {
            var end = Array.IndexOf(bytes, (byte)'\n', start);
            var record = bytes.AsSpan(start, (end < 0 ? bytes.Length : end) - start);
            start = end < 0 ? bytes.Length : end + 1;
            number++;

            if (number == 1 && record.SequenceEqual("---"u8))
            {
                frontMatter = true;
            }
            else if (frontMatter && record.SequenceEqual("---"u8))
            {
                frontMatter = false;
            }
            else if (frontMatter && record.StartsWith("updated:"u8))
            {
                Write(output, $"updated: {date}\n");
                continue;
            }

            output.Write(record);
            output.WriteByte((byte)'\n');
        }
    }

    private static void Write(MemoryStream output, string text) => output.Write(Utf8NoBom.GetBytes(text));
}
