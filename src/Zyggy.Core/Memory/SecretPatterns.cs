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

    [GeneratedRegex("([A-Z0-9]) ([0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex NoSpace();

    [GeneratedRegex("([0-9])[ -]([0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex NoSpaceNoHyphen();
}
