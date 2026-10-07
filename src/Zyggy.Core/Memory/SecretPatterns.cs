using System.Text;
using System.Text.RegularExpressions;

namespace Zyggy.Core.Memory;

/// <summary>The outcome of loading <c>secret-patterns.txt</c>: the patterns, or why they could not be read (fail closed).</summary>
internal sealed record SecretPatternsLoad(SecretPatterns? Patterns, string? Error);

/// <summary>
/// The 27 secret-pattern file applied in .NET: one <c>name&lt;TAB&gt;ERE[&lt;TAB&gt;flags]</c> per line, first match wins; flags
/// <c>icase</c>, <c>nospace</c> (also tried with spaces between an uppercase letter or digit and a digit removed) and
/// <c>nospace-nohyphen</c> (also tried with spaces and hyphens between digits removed). A pattern that times out counts as a match.
/// </summary>
internal sealed partial class SecretPatterns
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    private readonly IReadOnlyList<(string Name, Regex Pattern, string Flags)> _patterns;

    private SecretPatterns(IReadOnlyList<(string Name, Regex Pattern, string Flags)> patterns) => _patterns = patterns;

    public static SecretPatterns None { get; } = new([]);

    public int Count => _patterns.Count;

    public static SecretPatternsLoad Load(string path)
    {
        if (!File.Exists(path))
        {
            return new SecretPatternsLoad(null, $"{path} is missing");
        }

        try
        {
            var patterns = new List<(string, Regex, string)>();
            foreach (var line in File.ReadAllLines(path))
            {
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                var parts = line.Split('\t');
                if (parts.Length < 2)
                {
                    return new SecretPatternsLoad(null, $"{path}: malformed line '{parts[0]}'");
                }

                var flags = parts.Length > 2 ? parts[2] : string.Empty;
                var options = RegexOptions.CultureInvariant | (flags == "icase" ? RegexOptions.IgnoreCase : RegexOptions.None);
                patterns.Add((parts[0], new Regex(parts[1], options, MatchTimeout), flags));
            }

            return new SecretPatternsLoad(new SecretPatterns(patterns), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new SecretPatternsLoad(null, $"{path}: {ex.Message}");
        }
    }

    public bool TryMatch(string text, out string name)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (var (patternName, pattern, flags) in _patterns)
        {
            var variant = flags switch
            {
                "nospace" => Collapse(text, NoSpace()),
                "nospace-nohyphen" => Collapse(text, NoSpaceNoHyphen()),
                _ => text,
            };
            try
            {
                if (pattern.IsMatch(text) || pattern.IsMatch(variant))
                {
                    name = patternName;
                    return true;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                name = patternName;
                return true;
            }
        }

        name = string.Empty;
        return false;
    }

    /// <summary>
    /// Spec 35 AC-30: replaces every match of a number-shaped pattern (flag <c>nospace</c> or <c>nospace-nohyphen</c>) in
    /// <paramref name="line"/> by <c>[redacted: &lt;name&gt;]</c> — the whole token run, including the spaces or hyphens the variant joined —
    /// then re-tests the result against every pattern. <see langword="false"/> when no number-shaped pattern matches or anything still
    /// matches afterwards (the caller withholds the line whole).
    /// </summary>
    public bool TryRedactNumberShaped(string line, out string redacted, out IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(line);
        redacted = string.Empty;
        names = [];
        var spans = new List<(int Start, int End, string Name)>();
        foreach (var (patternName, pattern, flags) in _patterns)
        {
            if (flags is not ("nospace" or "nospace-nohyphen"))
            {
                continue;
            }

            var (variant, map) = Joined(line, hyphens: flags == "nospace-nohyphen");
            try
            {
                spans.AddRange(pattern.Matches(line).Where(m => m.Length > 0).Select(m => (m.Index, m.Index + m.Length, patternName)));
                spans.AddRange(pattern.Matches(variant).Where(m => m.Length > 0).Select(m => (map[m.Index], map[m.Index + m.Length - 1] + 1, patternName)));
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        if (spans.Count == 0)
        {
            return false;
        }

        // Overlapping spans (the raw and the joined match of one number) become one; the first pattern in file order names it.
        var merged = new List<(int Start, int End, string Name)>();
        foreach (var span in spans.OrderBy(s => s.Start).ThenByDescending(s => s.End))
        {
            if (merged.Count > 0 && span.Start < merged[^1].End)
            {
                merged[^1] = merged[^1] with { End = Math.Max(merged[^1].End, span.End) };
            }
            else
            {
                merged.Add(span);
            }
        }

        var builder = new StringBuilder(line.Length);
        var position = 0;
        foreach (var (start, end, name) in merged)
        {
            builder.Append(line, position, start - position).Append("[redacted: ").Append(name).Append(']');
            position = end;
        }

        builder.Append(line, position, line.Length - position);
        var result = builder.ToString();
        if (TryMatch(result, out _))
        {
            return false;
        }

        redacted = result;
        names = [.. merged.Select(s => s.Name).Distinct(StringComparer.Ordinal)];
        return true;
    }

    /// <summary>
    /// <c>zy_secret_match</c> over a text of several lines (<c>grep</c> per line): the first pattern, in file order, that matches any line.
    /// </summary>
    public bool TryMatchAnyLine(string text, out string name)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Split('\n');
        foreach (var (patternName, pattern, flags) in _patterns)
        {
            var single = new SecretPatterns([(patternName, pattern, flags)]);
            if (lines.Any(line => single.TryMatch(line, out _)))
            {
                name = patternName;
                return true;
            }
        }

        name = string.Empty;
        return false;
    }

    // sed ':a; s/(x) (y)/\1\2/; ta' — repeat until nothing changes.
    private static string Collapse(string text, Regex join)
    {
        string previous;
        do
        {
            previous = text;
            text = join.Replace(text, "$1$2");
        }
        while (text != previous);

        return text;
    }

    // The variant Collapse builds, with each kept character's index in the original: a single separator between a joinable character
    // and a digit is dropped (nospace: a space after A-Z or 0-9; nospace-nohyphen: a space or hyphen after 0-9). Removing one never makes
    // another removable, so one pass equals Collapse's repeat-until-stable.
    private static (string Variant, int[] Map) Joined(string text, bool hyphens)
    {
        var variant = new StringBuilder(text.Length);
        var map = new List<int>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var separator = c == ' ' || (hyphens && c == '-');
            var joinable = i > 0 && (char.IsAsciiDigit(text[i - 1]) || (!hyphens && char.IsAsciiLetterUpper(text[i - 1])));
            if (separator && joinable && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1]))
            {
                continue;
            }

            variant.Append(c);
            map.Add(i);
        }

        return (variant.ToString(), [.. map]);
    }

    [GeneratedRegex("([A-Z0-9]) ([0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex NoSpace();

    [GeneratedRegex("([0-9])[ -]([0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex NoSpaceNoHyphen();
}
