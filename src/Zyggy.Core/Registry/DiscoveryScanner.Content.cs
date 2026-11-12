using System.Globalization;
using System.Text;

using Zyggy.Core.Envelope;
using Zyggy.Core.Jobs;

namespace Zyggy.Core.Registry;

/// <summary>
/// The <see cref="DiscoveryDetail.Full"/> content of a project (spec 14, AC-2, AC-3, AC-9): subagents and skills with their front-matter
/// <c>description</c>, and the <c>CLAUDE.md</c> summary. Every published string is cleaned (<see cref="RegistryText.Clean"/>) and tested
/// against the secret patterns; a match publishes nothing for that string and adds a <see cref="ScanNoticeKind.SecretRedacted"/> notice
/// naming the pattern, never the text. A read failure publishes an empty description or no summary.
/// </summary>
public sealed partial class DiscoveryScanner
{
    private const int ReadLimitBytes = 64 * 1024;

    private static readonly UTF8Encoding LenientUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    private List<RegistryEntry> ReadAgents(string project, string path, List<ScanNotice> notices)
    {
        var folder = new DirectoryInfo(Path.Join(path, ".claude", "agents"));
        var files = new List<(string Name, FileInfo File)>();
        if (TryEnumerate(folder, d => d.EnumerateFiles(), out IEnumerable<FileInfo>? entries))
        {
            foreach (FileInfo file in entries)
            {
                string name = Path.GetFileNameWithoutExtension(file.Name);
                if (string.Equals(file.Extension, ".md", StringComparison.Ordinal) && !IsReparsePoint(file) && AgentNames.IsSafe(name))
                {
                    files.Add((name, file));
                }
            }
        }

        return Entries(project, "agents", files, notices);
    }

    private List<RegistryEntry> ReadSkills(string project, string path, List<ScanNotice> notices)
    {
        var folder = new DirectoryInfo(Path.Join(path, ".claude", "skills"));
        var files = new List<(string Name, FileInfo File)>();
        if (TryEnumerate(folder, d => d.EnumerateDirectories(), out IEnumerable<DirectoryInfo>? entries))
        {
            foreach (DirectoryInfo skill in entries)
            {
                var file = new FileInfo(Path.Join(skill.FullName, "SKILL.md"));
                if (!IsReparsePoint(skill) && AgentNames.IsSafe(skill.Name) && file.Exists && !IsReparsePoint(file))
                {
                    files.Add((skill.Name, file));
                }
            }
        }

        return Entries(project, "skills", files, notices);
    }

    private List<RegistryEntry> Entries(string project, string kind, List<(string Name, FileInfo File)> files, List<ScanNotice> notices)
    {
        files.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        if (files.Count > _options.MaxEntriesPerProject)
        {
            notices.Add(new ScanNotice(
                ScanNoticeKind.EntryCapReached,
                project,
                string.Create(CultureInfo.InvariantCulture, $"{kind}: {files.Count - _options.MaxEntriesPerProject} left out")));
            files.RemoveRange(_options.MaxEntriesPerProject, files.Count - _options.MaxEntriesPerProject);
        }

        var result = new List<RegistryEntry>(files.Count);
        foreach ((string name, FileInfo file) in files)
        {
            string description = Publishable(project, ReadDescription(file), notices) ?? string.Empty;
            result.Add(new RegistryEntry(name, description));
        }

        return result;
    }

    private static string ReadDescription(FileInfo file)
    {
        if (!TryReadHead(file.FullName, out byte[] head)
            || FrontMatterReader.Read(head, out FrontMatterMapping frontMatter, out _) is not null)
        {
            return string.Empty;
        }

        return frontMatter.Entries.TryGetValue("description", out FrontMatterNode? node) && node is FrontMatterScalar scalar
            ? scalar.Value
            : string.Empty;
    }

    private string? ReadSummary(string project, string path, List<ScanNotice> notices)
    {
        string file = Path.Join(path, "CLAUDE.md");
        if (!File.Exists(file) || !TryReadHead(file, out byte[] head))
        {
            return null;
        }

        string? line = SummaryLine(head, truncated: new FileInfo(file).Length > ReadLimitBytes);
        return line is null ? null : Publishable(project, line, notices);
    }

    /// <summary>
    /// The first line that is non-empty after trimming and is not a heading, list item, quote or fence; fenced blocks are skipped.
    /// A line cut by the 64 KiB read limit is not considered.
    /// </summary>
    private static string? SummaryLine(byte[] head, bool truncated)
    {
        string text = LenientUtf8.GetString(head);
        if (text.Length > 0 && text[0] == (char)0xFEFF)
        {
            text = text[1..];
        }

        string[] lines = text.Split('\n');
        int count = truncated ? lines.Length - 1 : lines.Length;
        bool inFence = false;
        for (int i = 0; i < count; i++)
        {
            string line = lines[i].Trim();
            if (line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence || line.Length == 0 || line[0] is '#' or '>' or '-' or '*' or '+' || IsNumberedItem(line))
            {
                continue;
            }

            return line;
        }

        return null;
    }

    private static bool IsNumberedItem(string line)
    {
        int i = 0;
        while (i < line.Length && char.IsAsciiDigit(line[i]))
        {
            i++;
        }

        return i > 0 && i < line.Length && line[i] is '.' or ')';
    }

    /// <summary>Cleans a harvested string and refuses it when it matches a secret pattern; <see langword="null"/> when nothing is publishable.</summary>
    private string? Publishable(string project, string text, List<ScanNotice> notices)
    {
        string cleaned = RegistryText.Clean(text);
        if (cleaned.Length == 0)
        {
            return null;
        }

        if (_patterns is not null && _patterns.TryMatch(cleaned, out string pattern))
        {
            notices.Add(new ScanNotice(ScanNoticeKind.SecretRedacted, project, pattern));
            return null;
        }

        return cleaned;
    }

    private static bool TryEnumerate<T>(DirectoryInfo folder, Func<DirectoryInfo, IEnumerable<T>> list, out IEnumerable<T> items)
    {
        items = [];
        try
        {
            if (!folder.Exists || IsReparsePoint(folder))
            {
                return false;
            }

            items = [.. list(folder)];
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool TryReadHead(string file, out byte[] head)
    {
        head = [];
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[(int)Math.Min(stream.Length, ReadLimitBytes)];
            stream.ReadExactly(buffer);
            head = buffer;
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return false;
        }
    }
}
