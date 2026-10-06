using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Zyggy.Core.Brief;

/// <summary>One basis of a suggestion: a memory file relative to the principal directory and a line copied from it.</summary>
internal sealed record IdeaBasis(string File, string Line);

/// <summary>One suggestion of the ideas run, as the model gave it (spec 35 "Ideas-run structured output").</summary>
internal sealed record IdeaSuggestion(string Id, string Area, string Text, string WhyNow, IReadOnlyList<IdeaBasis> Basis, DateOnly? Deadline, string? Prepare);

/// <summary>Parses the ideas run's structured output; the schema is not trusted, every field is checked again.</summary>
internal static partial class IdeasOutput
{
    public static (IReadOnlyList<IdeaSuggestion>? Suggestions, string? Rejection) TryParse(JsonElement? output)
    {
        if (output is not { ValueKind: JsonValueKind.Object } root)
        {
            return (null, "no structured output");
        }

        if (!root.TryGetProperty("suggestions", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return (null, "suggestions is not an array");
        }

        var suggestions = new List<IdeaSuggestion>();
        var n = 0;
        foreach (var item in list.EnumerateArray())
        {
            n++;
            var (suggestion, why) = Parse(item);
            if (suggestion is null)
            {
                return (null, $"suggestion {n}: {why}");
            }

            suggestions.Add(suggestion);
        }

        return (suggestions, null);
    }

    private static (IdeaSuggestion? Suggestion, string? Why) Parse(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return (null, "not an object");
        }

        string? Text(string name, int max) =>
            item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString()!.Length is > 0 and var length && length <= max ? v.GetString() : null;

        if (Text("id", 60) is not { } id || !Slug().IsMatch(id))
        {
            return (null, "id is not a slug");
        }

        if (Text("area", 40) is not { } area)
        {
            return (null, "area is missing");
        }

        if (Text("text", 160) is not { } text || Text("whyNow", 160) is not { } whyNow)
        {
            return (null, "text or whyNow is missing or too long");
        }

        if (!item.TryGetProperty("basis", out var basisList) || basisList.ValueKind != JsonValueKind.Array)
        {
            return (null, "basis is not an array");
        }

        var basis = new List<IdeaBasis>();
        foreach (var b in basisList.EnumerateArray())
        {
            if (b.ValueKind != JsonValueKind.Object
                || !b.TryGetProperty("file", out var file) || file.ValueKind != JsonValueKind.String
                || !b.TryGetProperty("line", out var line) || line.ValueKind != JsonValueKind.String)
            {
                return (null, "basis entry is not {file, line}");
            }

            basis.Add(new IdeaBasis(file.GetString()!, line.GetString()!));
        }

        if (basis.Count == 0)
        {
            return (null, "basis is empty");
        }

        DateOnly? deadline = null;
        if (item.TryGetProperty("deadline", out var d) && d.ValueKind != JsonValueKind.Null)
        {
            if (d.ValueKind != JsonValueKind.String || !DateOnly.TryParseExact(d.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                return (null, "deadline is not a date");
            }

            deadline = parsed;
        }

        string? prepare = null;
        if (item.TryGetProperty("prepare", out var p) && p.ValueKind != JsonValueKind.Null)
        {
            if (p.ValueKind != JsonValueKind.String || p.GetString()!.Length > 160)
            {
                return (null, "prepare is not a short text or null");
            }

            prepare = p.GetString()!.Length == 0 ? null : p.GetString();
        }

        return (new IdeaSuggestion(id, area, text, whyNow, basis, deadline, prepare), null);
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,59}$", RegexOptions.CultureInvariant)]
    private static partial Regex Slug();
}
