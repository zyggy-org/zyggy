using System.Text;

namespace Zyggy.Core.Tests.Infrastructure;

/// <summary>
/// Text-level mutations of a canonical-style envelope file (LF front matter): replace, remove or add a top-level line,
/// swap the body, prefix a BOM. It never goes through the code under test.
/// </summary>
public sealed class EnvelopeText
{
    private readonly List<string> _lines;
    private readonly byte[] _body;
    private readonly bool _bom;

    private EnvelopeText(List<string> lines, byte[] body, bool bom)
    {
        _lines = lines;
        _body = body;
        _bom = bom;
    }

    public static EnvelopeText From(byte[] md)
    {
        string text = Encoding.UTF8.GetString(md);
        if (!text.StartsWith("---\n", StringComparison.Ordinal))
        {
            throw new ArgumentException("Only canonical-style (LF) golden files can be mutated.", nameof(md));
        }

        int close = text.IndexOf("\n---\n", 3, StringComparison.Ordinal);
        string frontMatter = text[4..(close + 1)];
        byte[] body = Encoding.UTF8.GetBytes(text[(close + 5)..]);
        var lines = frontMatter.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        return new EnvelopeText(lines, body, bom: false);
    }

    /// <summary>Replaces the value of top-level <paramref name="key"/> (the key must exist).</summary>
    public EnvelopeText WithValue(string key, string value)
    {
        int index = IndexOf(key);
        var lines = new List<string>(_lines) { [index] = key + ": " + value };
        return new EnvelopeText(lines, _body, _bom);
    }

    /// <summary>Removes the top-level line of <paramref name="key"/> (the key must exist).</summary>
    public EnvelopeText WithoutKey(string key)
    {
        var lines = new List<string>(_lines);
        lines.RemoveAt(IndexOf(key));
        return new EnvelopeText(lines, _body, _bom);
    }

    /// <summary>Appends a raw front-matter line before the closing delimiter.</summary>
    public EnvelopeText WithLine(string line) => new([.. _lines, line], _body, _bom);

    /// <summary>Appends <c>key: value</c> before the closing delimiter.</summary>
    public EnvelopeText WithKey(string key, string value) => WithLine(key + ": " + value);

    public EnvelopeText WithBody(byte[] body) => new(_lines, body, _bom);

    public EnvelopeText WithBomPrefix() => new(_lines, _body, bom: true);

    /// <summary>Returns the file with every front-matter line ending in <paramref name="newline"/>.</summary>
    public byte[] ToBytes(string newline = "\n")
    {
        var builder = new StringBuilder();
        builder.Append("---").Append(newline);
        foreach (string line in _lines)
        {
            builder.Append(line).Append(newline);
        }

        builder.Append("---").Append(newline);
        byte[] head = Encoding.UTF8.GetBytes(builder.ToString());
        byte[] bom = _bom ? [0xEF, 0xBB, 0xBF] : [];
        return [.. bom, .. head, .. _body];
    }

    private int IndexOf(string key)
    {
        int index = _lines.FindIndex(l => l.StartsWith(key + ":", StringComparison.Ordinal));
        return index >= 0 ? index : throw new ArgumentException($"Key '{key}' is not a top-level line.", nameof(key));
    }
}
