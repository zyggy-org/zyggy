using System.Globalization;
using System.Text;

using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Zyggy.Core.Memory;

/// <summary>Reads a memory file: the YAML front matter between the first two <c>---</c> lines, then the body lines.</summary>
internal static class MemoryFileReader
{
    public static MemoryFile Read(string path) => Parse(Encoding.UTF8.GetString(File.ReadAllBytes(path)));

    /// <exception cref="FormatException">The front matter is not a YAML mapping.</exception>
    public static MemoryFile Parse(string text)
    {
        var lines = TextLines.Split(text);
        if (lines.Count == 0 || lines[0] != "---")
        {
            return new MemoryFile(null, null, [], null, new Dictionary<string, string>(), false, lines);
        }

        var close = lines.IndexOf("---", 1);
        if (close < 0)
        {
            throw new FormatException("The front matter has no closing --- line.");
        }

        var mapping = ParseMapping(string.Join('\n', lines.Skip(1).Take(close - 1)));
        string? name = null, description = null;
        DateOnly? updated = null;
        var aliases = new List<string>();
        var unknown = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (keyNode, valueNode) in mapping.Children)
        {
            var key = ((YamlScalarNode)keyNode).Value ?? string.Empty;
            switch (key)
            {
                case "name":
                    name = Scalar(valueNode);
                    break;
                case "description":
                    description = Scalar(valueNode);
                    break;
                case "updated":
                    updated = DateOnly.TryParseExact(Scalar(valueNode), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                        ? d
                        : null;
                    break;
                case "aliases" when valueNode is YamlSequenceNode sequence:
                    aliases.AddRange(sequence.Children.Select(Scalar).OfType<string>());
                    break;
                case "aliases":
                    aliases.AddRange(Scalar(valueNode) is { Length: > 0 } single ? [single] : []);
                    break;
                default:
                    unknown[key] = Scalar(valueNode) ?? valueNode.ToString();
                    break;
            }
        }

        return new MemoryFile(name, description, aliases, updated, unknown, true, lines.Skip(close + 1).ToList());
    }

    private static YamlMappingNode ParseMapping(string yaml)
    {
        if (yaml.Trim().Length == 0)
        {
            return new YamlMappingNode();
        }

        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            return stream.Documents.Count == 1 && stream.Documents[0].RootNode is YamlMappingNode mapping
                ? mapping
                : throw new FormatException("The front matter is not a YAML mapping.");
        }
        catch (YamlException ex)
        {
            throw new FormatException("The front matter is not valid YAML.", ex);
        }
    }

    private static string? Scalar(YamlNode node) => node is YamlScalarNode scalar ? scalar.Value : null;
}
