using System.Globalization;
using System.Reflection;
using System.Text;

using Zyggy.Core.Memory;

namespace Zyggy.Core.Dream;

/// <summary>
/// The model contract embedded in <c>Zyggy.Core</c> (OQ-3): three prompts and three draft-07 schemas, and the rendering of a
/// batch as data. Every memory and inbox line goes between <c>&lt;&lt;&lt;</c> and <c>&gt;&gt;&gt;</c> lines; a data line can never
/// contain either delimiter.
/// </summary>
internal sealed class DreamPrompts
{
    public DreamPrompts()
    {
        FilingPrompt = Load("filing.prompt.md");
        FilingSchema = Load("filing.schema.json");
        CompressionPrompt = Load("compression.prompt.md");
        CompressionSchema = Load("compression.schema.json");
        MigrationPrompt = Load("migration.prompt.md");
        MigrationSchema = Load("migration.schema.json");
    }

    public string FilingPrompt { get; }

    public string FilingSchema { get; }

    public string CompressionPrompt { get; }

    public string CompressionSchema { get; }

    public string MigrationPrompt { get; }

    public string MigrationSchema { get; }

    public static string RenderFilingInput(DreamBatch batch, MemorySnapshot snapshot, DateOnly runDate) =>
        RenderFilingInput(batch, new WorkingSet(snapshot), runDate);

    public static string RenderFilingInput(DreamBatch batch, WorkingSet set, DateOnly runDate)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(set);
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"Run date: {runDate:yyyy-MM-dd}\n");
        text.Append(CultureInfo.InvariantCulture, $"Memory directory (read-only; Read, Grep and Glob work there): {set.Snapshot.Paths.PrincipalDirectory}\n\n");

        var (categories, files) = Index(set);
        Block(text, "Categories (data)", categories);
        Block(text, "Memory files (data)", files);
        Block(text, "Lines to file (data)", batch.Lines.Select(l => $"{l.Id} {l.RelativePath}: {l.Text}"));
        text.Append("Return one disposition per line id.\n");
        return text.ToString();
    }

    public static string RenderCompressionInput(string relativePath, IReadOnlyList<string> bodyLines)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"File: {relativePath}\nBody lines: {bodyLines.Count}\n\n");
        Block(text, "Body (data)", bodyLines);
        return text.ToString();
    }

    public static string RenderMigrationInput(IEnumerable<(string Path, string Description)> legacyFiles)
    {
        var text = new StringBuilder();
        Block(text, "Legacy files (data)", legacyFiles.Select(f => $"{f.Path} — {f.Description}"));
        return text.ToString();
    }

    internal static (List<string> Categories, List<string> Files) Index(WorkingSet set)
    {
        var categories = new List<string>();
        var files = new List<string>();
        var bySide = set.Paths
            .Select(p => (Path: p, Segments: p.Split('/')))
            .Where(p => p.Segments.Length == 3 && MemorySideWire.TryFromWire(p.Segments[0], out _) && CategoryName.TryParse(p.Segments[1], out _))
            .GroupBy(p => (p.Segments[0], p.Segments[1]))
            .OrderBy(g => g.Key.Item1 == "private" ? 0 : 1)
            .ThenBy(g => g.Key.Item2, StringComparer.Ordinal);
        foreach (var category in bySide)
        {
            var members = category.Where(p => !p.Segments[2].StartsWith('_') && p.Segments[2].EndsWith(".md", StringComparison.Ordinal))
                .OrderBy(p => p.Path, StringComparer.Ordinal)
                .ToList();
            var index = $"{category.Key.Item1}/{category.Key.Item2}/_index.md";
            categories.Add($"{category.Key.Item1}/{category.Key.Item2}/ — {Description(set, index)} ({members.Count} files)");
            files.AddRange(members.Select(m => $"{m.Path} — {Description(set, m.Path)}"));
        }

        return (categories, files);
    }

    private static string Description(WorkingSet set, string path)
    {
        if (set.Text(path) is not { } text)
        {
            return "(no description)";
        }

        try
        {
            return MemoryFileReader.Parse(text).Description is { Length: > 0 } description ? description : "(no description)";
        }
        catch (FormatException)
        {
            return "(no description)";
        }
    }

    private static void Block(StringBuilder text, string title, IEnumerable<string> lines)
    {
        text.Append("## ").Append(title).Append("\n<<<\n");
        foreach (var line in lines)
        {
            text.Append(Neutralise(line)).Append('\n');
        }

        text.Append(">>>\n\n");
    }

    // A data line must never open or close a data block.
    private static string Neutralise(string line) =>
        line.Replace("<<<", "‹‹‹", StringComparison.Ordinal).Replace(">>>", "›››", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal);

    private static string Load(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Zyggy.Core.Dream.Prompts." + name)
            ?? throw new InvalidOperationException($"The embedded resource {name} is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
