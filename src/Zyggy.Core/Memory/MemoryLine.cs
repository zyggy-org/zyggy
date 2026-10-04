using System.Globalization;
using System.Text.RegularExpressions;

namespace Zyggy.Core.Memory;

/// <summary>The tag of a memory line.</summary>
internal enum MemoryTag
{
    Stated,
    Observed,
    Other,
}

/// <summary>
/// One body line in the 27 format: <c>- [stated] YYYY-MM-DD[ (scope)]: fact</c>,
/// <c>- [observed] YYYY-MM-DD [p1; p2]: fact</c>, or the Stop-hook form <c>- [observed] HH:MM session &lt;id&gt;: fact</c>.
/// </summary>
internal sealed partial record MemoryLine(
    string Text,
    MemoryTag Tag,
    DateOnly? Date,
    string? Time,
    string? Session,
    string? Scope,
    IReadOnlyList<string> Provenance,
    string? Fact)
{
    /// <summary>The longest line, in characters, a memory file may hold (spec 28 Memory layout).</summary>
    public const int MaxLength = 400;

    public bool TooLong => Text.Length > MaxLength;

    public static MemoryLine Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Stated().Match(text) is { Success: true } stated && TryDate(stated.Groups["date"].Value, out var statedDate))
        {
            var scope = stated.Groups["scope"].Success ? stated.Groups["scope"].Value : null;
            return new MemoryLine(text, MemoryTag.Stated, statedDate, null, null, scope, [], stated.Groups["fact"].Value);
        }

        if (Observed().Match(text) is { Success: true } observed && TryDate(observed.Groups["date"].Value, out var observedDate))
        {
            var provenance = observed.Groups["prov"].Success
                ? observed.Groups["prov"].Value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                : [];
            return new MemoryLine(text, MemoryTag.Observed, observedDate, null, null, null, provenance, observed.Groups["fact"].Value);
        }

        if (SessionForm().Match(text) is { Success: true } session)
        {
            return new MemoryLine(text, MemoryTag.Observed, null, session.Groups["time"].Value, session.Groups["id"].Value, null, [],
                session.Groups["fact"].Value);
        }

        return new MemoryLine(text, MemoryTag.Other, null, null, null, null, [], null);
    }

    private static bool TryDate(string text, out DateOnly date) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    [GeneratedRegex(@"^- \[stated\] (?<date>\d{4}-\d{2}-\d{2})(?: \((?<scope>[^)]*)\))?: (?<fact>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex Stated();

    [GeneratedRegex(@"^- \[observed\] (?<date>\d{4}-\d{2}-\d{2})(?: \[(?<prov>[^\]]*)\])?: (?<fact>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex Observed();

    [GeneratedRegex(@"^- \[observed\] (?<time>\d{2}:\d{2}) session (?<id>[^\s:]+): (?<fact>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex SessionForm();
}
