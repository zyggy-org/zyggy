using System.Security.Cryptography;
using System.Text;

using Zyggy.Core.Memory;

namespace Zyggy.Core.Dream;

/// <summary>One file of the principal's tree as read at snapshot time.</summary>
internal sealed record SnapshotFile(string RelativePath, byte[] Bytes, string Sha256, DateTime LastWriteUtc)
{
    public string Text => Encoding.UTF8.GetString(Bytes);

    /// <summary>The parsed memory file for a <c>.md</c> file with readable front matter; otherwise <see langword="null"/>.</summary>
    public MemoryFile? Parsed { get; init; }
}

/// <summary>Every file under the principal directory, read once into memory at the start of a run (or a batch); archived items are listed by size only.</summary>
internal sealed class MemorySnapshot
{
    private readonly Dictionary<string, SnapshotFile> _files;

    private MemorySnapshot(MemoryPaths paths, Dictionary<string, SnapshotFile> files, Dictionary<string, long> archiveItems)
    {
        Paths = paths;
        _files = files;
        ArchiveItems = archiveItems;
    }

    public MemoryPaths Paths { get; }

    public IReadOnlyDictionary<string, SnapshotFile> Files => _files;

    /// <summary>Archived items (spec 37 AC-21) by relative path, with their size: listed, never read.</summary>
    public IReadOnlyDictionary<string, long> ArchiveItems { get; }

    public static MemorySnapshot Load(MemoryPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var files = new Dictionary<string, SnapshotFile>(StringComparer.Ordinal);
        var archiveItems = new Dictionary<string, long>(StringComparer.Ordinal);
        if (Directory.Exists(paths.PrincipalDirectory))
        {
            foreach (var full in Directory.EnumerateFiles(paths.PrincipalDirectory, "*", SearchOption.AllDirectories))
            {
                if (full.Contains(".zyggy-tmp-", StringComparison.Ordinal))
                {
                    continue;
                }

                var relative = paths.Relative(full);
                if (paths.TryResolve(relative) is { Succeeded: true, Area: MemoryArea.ArchiveItem })
                {
                    archiveItems[relative] = new FileInfo(full).Length;
                    continue;
                }

                var bytes = File.ReadAllBytes(full);
                MemoryFile? parsed = null;
                if (relative.EndsWith(".md", StringComparison.Ordinal))
                {
                    try
                    {
                        parsed = MemoryFileReader.Parse(Encoding.UTF8.GetString(bytes));
                    }
                    catch (FormatException)
                    {
                        // A file whose front matter does not parse is still a file; its lines are read raw.
                    }
                }

                files[relative] = new SnapshotFile(relative, bytes, Hash(bytes), File.GetLastWriteTimeUtc(full)) { Parsed = parsed };
            }
        }

        return new MemorySnapshot(paths, files, archiveItems);
    }

    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>The body lines of a file (after the front matter) that start with <c>- </c>, with their 1-based line numbers.</summary>
    public static IEnumerable<(int Number, string Text)> BodyLines(string text)
    {
        var lines = TextLines.Split(text);
        var start = 0;
        if (lines.Count > 0 && lines[0] == "---")
        {
            var close = lines.IndexOf("---", 1);
            start = close < 0 ? lines.Count : close + 1;
        }

        for (var i = start; i < lines.Count; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                yield return (i + 1, line);
            }
        }
    }
}
