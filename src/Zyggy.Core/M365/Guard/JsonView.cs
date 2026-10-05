using System.Text;
using System.Text.Json;

namespace Zyggy.Core.M365.Guard;

/// <summary>
/// A JSON object as jq sees it: a key given twice in the same case is one key (the last value wins, as jq parses), keys sort by code
/// point, and <see cref="CiGet"/> matches a key case-insensitively (ASCII) like the scripts' <c>ci_get</c>.
/// </summary>
internal sealed class JsonView
{
    private readonly List<(string Name, JsonElement Value)> _properties = [];

    private JsonView(JsonElement element)
    {
        foreach (var property in element.EnumerateObject())
        {
            var index = _properties.FindIndex(p => p.Name == property.Name);
            if (index >= 0)
            {
                _properties[index] = (property.Name, property.Value);
            }
            else
            {
                _properties.Add((property.Name, property.Value));
            }
        }
    }

    /// <summary>The object's view, or <see langword="null"/> when <paramref name="element"/> is not an object.</summary>
    public static JsonView? Of(JsonElement? element) => element is { ValueKind: JsonValueKind.Object } e ? new JsonView(e) : null;

    /// <summary>jq <c>keys</c>: the names, sorted by code point.</summary>
    public IEnumerable<string> Keys => _properties.Select(p => p.Name).Order(StringComparer.Ordinal);

    /// <summary><c>.name</c>: the exact key, or <see langword="null"/>.</summary>
    public JsonElement? Get(string name) => _properties.FindIndex(p => p.Name == name) is var i and >= 0 ? _properties[i].Value : null;

    /// <summary><c>ci_get</c>: the first value whose ASCII-lower-cased key is <paramref name="lowerKey"/>, or <see langword="null"/>.</summary>
    public JsonElement? CiGet(string lowerKey) =>
        _properties.FindIndex(p => AsciiLower(p.Name) == lowerKey) is var i and >= 0 ? _properties[i].Value : null;

    /// <summary><c>ci_unique</c>: no two keys equal after ASCII lower-casing.</summary>
    public bool CiUnique => _properties.Select(p => AsciiLower(p.Name)).Distinct(StringComparer.Ordinal).Count() == _properties.Count;

    public IEnumerable<JsonElement> Values => _properties.Select(p => p.Value);

    /// <summary><c>.x // "" | strings</c>: the string value, else <c>""</c>.</summary>
    public string StringOrEmpty(string name) => Get(name) is { ValueKind: JsonValueKind.String } s ? s.GetString()! : string.Empty;

    /// <summary>jq <c>ascii_downcase</c> and bash <c>${x,,}</c> under <c>LC_ALL=C</c>: A–Z only.</summary>
    public static string AsciiLower(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            builder.Append(c is >= 'A' and <= 'Z' ? (char)(c + 32) : c);
        }

        return builder.ToString();
    }

    /// <summary>jq truthiness: anything but <c>null</c>, <c>false</c> and a missing value.</summary>
    public static bool IsTruthy(JsonElement? element) => element is { ValueKind: not (JsonValueKind.Null or JsonValueKind.False or JsonValueKind.Undefined) };
}
