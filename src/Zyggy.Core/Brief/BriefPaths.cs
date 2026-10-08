using System.Globalization;
using System.Text.RegularExpressions;

namespace Zyggy.Core.Brief;

/// <summary>The kind of a file in the brief state directory, by its name.</summary>
internal enum BriefFileKind
{
    Markdown,
    Sidecar,
}

/// <summary>
/// Every path of the brief state directory (spec 35 Contracts "Files"): <c>&lt;ZYGGY_STATE_DIR or ~/.local/state/zyggy&gt;/brief</c> — the same
/// root rule as <see cref="M365.M365Paths"/> — and its closed set of file names. The only builder of brief paths.
/// </summary>
internal sealed partial class BriefPaths
{
    public BriefPaths(IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        string? Get(string key) => environment.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value) ? value : null;

        var home = Get("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Directory = Path.Join(Get("ZYGGY_STATE_DIR") ?? Path.Join(home, ".local", "state", "zyggy"), "brief");
    }

    private BriefPaths(string directory) => Directory = directory;

    /// <summary>The brief directory next to the m365 one (<c>&lt;state&gt;/brief</c> beside <c>&lt;state&gt;/m365</c>), for the brief run.</summary>
    public static BriefPaths Beside(M365.M365Paths m365)
    {
        ArgumentNullException.ThrowIfNull(m365);
        return new BriefPaths(Path.Join(Path.GetDirectoryName(m365.StateDirectory), "brief"));
    }

    /// <summary>Gets the brief state directory, 0700 on Linux.</summary>
    public string Directory { get; }

    /// <summary>Gets <c>last-shown</c>: the newest brief <c>zyggy brief show</c> has printed.</summary>
    public string LastShown => Path.Join(Directory, "last-shown");

    /// <summary>
    /// Gets <c>brief.request</c> in the state root (beside the <c>brief</c> and <c>m365</c> directories, as <c>dream.request</c> is): written by
    /// <c>zyggy brief request</c>, watched by the instance's <c>zyggy-morning-brief.path</c> unit, deleted by <c>zyggy m365 brief</c> before it runs.
    /// </summary>
    public string Request => Path.Join(Path.GetDirectoryName(Directory), "brief.request");

    /// <summary>Gets <c>ideas.jsonl</c>: the suggestions shown and the owner's answers.</summary>
    public string IdeasLog => Path.Join(Directory, "ideas.jsonl");

    /// <summary>Gets <c>runs/</c>: the ideas run's empty working directories.</summary>
    public string RunsDirectory => Path.Join(Directory, "runs");

    /// <summary>Returns <c>brief-&lt;date&gt;.md</c>: the rendered brief, complete form with page markers.</summary>
    public string Markdown(DateOnly date) => Path.Join(Directory, $"brief-{Iso(date)}.md");

    /// <summary>Returns <c>brief-&lt;date&gt;.json</c>: the item list and counts the session acts on.</summary>
    public string Sidecar(DateOnly date) => Path.Join(Directory, $"brief-{Iso(date)}.json");

    /// <summary>Parses a brief file name (<c>brief-YYYY-MM-DD.md</c> or <c>.json</c>); anything else is not a brief file.</summary>
    public static bool TryParseDate(string fileName, out DateOnly date, out BriefFileKind kind)
    {
        var match = FileName().Match(fileName);
        if (match.Success && DateOnly.TryParseExact(match.Groups["date"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            kind = match.Groups["ext"].Value == "md" ? BriefFileKind.Markdown : BriefFileKind.Sidecar;
            return true;
        }

        date = default;
        kind = default;
        return false;
    }

    public static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"\Abrief-(?<date>[0-9]{4}-[0-9]{2}-[0-9]{2})\.(?<ext>md|json)\z", RegexOptions.CultureInvariant)]
    private static partial Regex FileName();
}
