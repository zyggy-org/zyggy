namespace Zyggy.Core.Memory;

/// <summary>One durable memory file, <c>&lt;side&gt;/&lt;category&gt;/&lt;slug&gt;.md</c>, as the Hub's <c>get_context</c> sees it.</summary>
/// <param name="RelativePath">The path under the principal directory, with <c>/</c> separators.</param>
/// <param name="Side">The side the file lives on.</param>
/// <param name="Category">The category directory.</param>
/// <param name="Slug">The file name without <c>.md</c>.</param>
/// <param name="File">The parsed front matter and body.</param>
internal sealed record MemoryEntry(string RelativePath, MemorySide Side, CategoryName Category, Slug Slug, MemoryFile File);

/// <summary>One body line of <c>daily/&lt;date&gt;.md</c>, as written.</summary>
/// <param name="Date">The date in the file name.</param>
/// <param name="Text">The line, without its line ending.</param>
internal sealed record DailyLine(DateOnly Date, string Text);
