using System.Globalization;

namespace Zyggy.Core.Memory;

/// <summary>The one <c>[stated]</c> line that indexes an archived item (spec 37 AC-14) and the one that records its removal.</summary>
internal static class ArchiveIndexLine
{
    /// <summary>The longest line the dream files unchanged (= <see cref="MemoryLine.MaxLength"/>).</summary>
    public const int MaxLength = 400;

    public static string Added(DateOnly date, ArchiveAddRequest request, ArchiveMediaType type, long sizeBytes)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Prefix(date, request.Project) + AddedFact(request, type, sizeBytes);
    }

    /// <summary>The fact part of <see cref="Added"/>: what <c>memory remember --scope project:&lt;project&gt; -- "&lt;fact&gt;"</c> is given.</summary>
    public static string AddedFact(ArchiveAddRequest request, ArchiveMediaType type, long sizeBytes)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Fact(request, ArchiveMediaTypeWire.ToWire(type), ArchiveSize.Format(sizeBytes), ArchiveMediaTypeWire.Extension(type));
    }

    /// <summary>The longest line this request can produce: the longest media type, a three-digit megabyte size, a three-letter extension.</summary>
    public static string Longest(DateOnly date, ArchiveAddRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Prefix(date, request.Project) + Fact(request, ArchiveMediaTypeWire.ToWire(ArchiveMediaType.Pdf), "999.9 MB", "pdf");
    }

    public static string Removed(DateOnly date, Slug project, Slug slug, string extension, string name)
    {
        ArgumentNullException.ThrowIfNull(project);
        return Prefix(date, project) + RemovedFact(project, slug, extension, name);
    }

    /// <summary>The fact part of <see cref="Removed"/>.</summary>
    public static string RemovedFact(Slug project, Slug slug, string extension, string name)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(slug);
        return $"Removed archived item archive/{project.Value}/{slug.Value}.{extension} (\"{name}\")";
    }

    private static string Prefix(DateOnly date, Slug project) => $"- [stated] {Date(date)} (project:{project.Value}): ";

    private static string Fact(ArchiveAddRequest r, string mediaType, string size, string extension) =>
        $"Archived \"{r.Name}\" ({mediaType}, {size}) at archive/{r.Project.Value}/{r.Slug.Value}.{extension} — {r.Description}";

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>The size of an item as the index line and <c>list</c> print it: <c>&lt;n&gt; B</c>, <c>&lt;n.n&gt; KB</c> or <c>&lt;n.n&gt; MB</c> (1024-based).</summary>
internal static class ArchiveSize
{
    public static string Format(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        return bytes switch
        {
            < 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes} B"),
            < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.0} KB"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024.0):0.0} MB"),
        };
    }
}
