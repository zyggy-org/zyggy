using System.Text;

namespace Zyggy.Core.Memory;

/// <summary>
/// The Hub's read-only view of one principal's memory (spec 11, D3): the durable <c>&lt;side&gt;/&lt;category&gt;/&lt;slug&gt;.md</c>
/// files and the daily lines of a window. Nothing else is ever a candidate — not <c>inbox/</c>, <c>auto/</c>, <c>archive/</c>,
/// <c>.dream/</c>, the legacy directories, the monthly roll-ups nor the identity files. Every path comes from
/// <see cref="MemoryPaths"/>, and each file is confirmed by <see cref="MemoryPaths.TryResolve"/> so a link leading out is skipped.
/// </summary>
/// <param name="paths">The principal's memory paths.</param>
internal sealed class MemoryStore(MemoryPaths paths)
{
    /// <summary>Returns every durable file on both sides, ordered by relative path; a bad front matter never throws.</summary>
    public IReadOnlyList<MemoryEntry> DurableEntries()
    {
        var entries = new List<MemoryEntry>();
        foreach (var side in Enum.GetValues<MemorySide>())
        {
            if (!Directory.Exists(paths.Side(side)))
            {
                continue;
            }

            foreach (var directory in Directory.EnumerateDirectories(paths.Side(side)))
            {
                if (!CategoryName.TryParse(Path.GetFileName(directory), out var category))
                {
                    continue;
                }

                foreach (var candidate in Directory.EnumerateFiles(paths.Category(side, category), "*.md"))
                {
                    var name = Path.GetFileName(candidate);
                    if (name.StartsWith('_')
                        || !string.Equals(Path.GetExtension(name), ".md", StringComparison.Ordinal)
                        || !Slug.TryParse(Path.GetFileNameWithoutExtension(name), out var slug))
                    {
                        continue;
                    }

                    var resolution = paths.TryResolve(paths.Relative(paths.File(side, category, slug)));
                    if (!resolution.Succeeded || resolution.Area != MemoryArea.Durable || ReadText(resolution.FullPath!) is not { } text)
                    {
                        continue;
                    }

                    entries.Add(new MemoryEntry(resolution.RelativePath!, side, category, slug, Parse(text)));
                }
            }
        }

        return entries.OrderBy(entry => entry.RelativePath, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Returns the non-empty body lines of <c>daily/YYYY-MM-DD.md</c> for each date from <paramref name="today"/> back to
    /// <paramref name="today"/> − <paramref name="windowDays"/>, newest file first, each file in its own order. Only those
    /// dated names are probed, so the monthly roll-ups and any other file in <c>daily/</c> contribute nothing.
    /// </summary>
    public IReadOnlyList<DailyLine> DailyLines(DateOnly today, int windowDays)
    {
        var lines = new List<DailyLine>();
        for (var date = today; date >= today.AddDays(-windowDays); date = date.AddDays(-1))
        {
            var full = paths.Daily(date);
            if (!File.Exists(full))
            {
                continue;
            }

            var resolution = paths.TryResolve(paths.Relative(full));
            if (!resolution.Succeeded || resolution.Area != MemoryArea.Daily || ReadText(resolution.FullPath!) is not { } text)
            {
                continue;
            }

            var body = TextLines.Split(text);
            var start = 0;
            if (body.Count > 0 && body[0].TrimEnd('\r') == "---")
            {
                var close = body.FindIndex(1, line => line.TrimEnd('\r') == "---");
                start = close < 0 ? 0 : close + 1;
            }

            lines.AddRange(body.Skip(start)
                .Select(line => line.TrimEnd('\r'))
                .Where(line => line.Trim().Length > 0)
                .Select(line => new DailyLine(date, line)));
        }

        return lines;
    }

    private static MemoryFile Parse(string text)
    {
        try
        {
            return MemoryFileReader.Parse(text);
        }
        catch (FormatException)
        {
            return new MemoryFile(null, null, [], null, new Dictionary<string, string>(), false, TextLines.Split(text));
        }
    }

    // A file that vanished or cannot be read between the listing and the read is skipped, never an error to the caller.
    private static string? ReadText(string fullPath)
    {
        try
        {
            return Encoding.UTF8.GetString(File.ReadAllBytes(fullPath));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
