using System.Globalization;
using System.Text;

using Zyggy.Core.Dream;

namespace Zyggy.Core.Memory;

/// <summary>
/// The Hub's read-only view of one principal's memory (spec 11, D3): the durable <c>&lt;side&gt;/&lt;category&gt;/&lt;slug&gt;.md</c>
/// files, the daily lines of a window and (spec 13 decision Q2) the not-yet-filed lines of <c>inbox/bus-*.md</c> and <c>inbox/remember-*.md</c>.
/// Nothing else is ever a candidate — not the other inbox files, <c>auto/</c>, <c>archive/</c>,
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

    /// <summary>
    /// The dream's ledger (plan 13 A11): empty when the file is missing, <see langword="null"/> when it is unreadable, invalid or of
    /// another schema — which <see cref="PickedUpLines"/> treats as "nothing consumed".
    /// </summary>
    public DreamLedger? Ledger()
    {
        if (!File.Exists(paths.Ledger))
        {
            return DreamLedger.Empty();
        }

        return ReadText(paths.Ledger) is { } text ? DreamLedger.Load(text).Ledger : null;
    }

    /// <summary>
    /// The not-yet-filed lines of <c>inbox/bus-*.md</c> and <c>inbox/remember-*.md</c> (spec 13 decision Q2): only the <c>- </c> body
    /// lines the dream hashes, minus those <paramref name="ledger"/> consumed; newest file-name date first, then the name descending,
    /// lines in file order. Each file is confirmed by <see cref="MemoryPaths.TryResolve"/>, so a link leading out is skipped.
    /// </summary>
    public IReadOnlyList<InboxLine> PickedUpLines(DreamLedger? ledger, ContextOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Directory.Exists(paths.InboxDirectory))
        {
            return [];
        }

        var files = new List<(string Name, DateOnly? Date, string Text)>();
        foreach (var candidate in Directory.EnumerateFiles(paths.InboxDirectory, "*.md"))
        {
            var name = Path.GetFileName(candidate);
            if (name.StartsWith('_')
                || !string.Equals(Path.GetExtension(name), ".md", StringComparison.Ordinal)
                || !options.InboxFilePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            {
                continue;
            }

            var resolution = paths.TryResolve(paths.Relative(paths.InboxFile(name)));
            if (!resolution.Succeeded || resolution.Area != MemoryArea.Inbox || ReadText(resolution.FullPath!) is not { } text)
            {
                continue;
            }

            files.Add((name, FileDate(name), text));
        }

        return files
            .OrderByDescending(file => file.Date)
            .ThenByDescending(file => file.Name, StringComparer.Ordinal)
            .SelectMany(file => MemorySnapshot.BodyLines(file.Text)
                .Where(line => ledger?.IsConsumed("inbox/" + file.Name, LineHash.Of(line.Text)) != true)
                .Select(line => new InboxLine(file.Name, file.Date, line.Text)))
            .ToList();
    }

    private static DateOnly? FileDate(string name) =>
        name.Length > 13 && DateOnly.TryParseExact(name[^13..^3], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) && name[^14] == '-'
            ? date
            : null;

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
