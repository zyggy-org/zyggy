namespace Zyggy.Core.Memory;

/// <summary>The single mapping between <see cref="ArchiveMediaType"/>, its media-type string, its stored extension and its index-line label.</summary>
public static class ArchiveMediaTypeWire
{
    private static readonly ArchiveMediaType[] Members = Enum.GetValues<ArchiveMediaType>();

    /// <summary>Gets every member, in declaration order.</summary>
    public static IReadOnlyList<ArchiveMediaType> All => Members;

    /// <summary>Returns the media-type string of a member.</summary>
    /// <param name="type">The media type.</param>
    /// <returns>
    /// <c>text/plain</c>, <c>text/markdown</c>, <c>application/pdf</c>, <c>image/png</c>, <c>image/jpeg</c>, <c>image/gif</c>, or the
    /// IANA Office Open XML string of a Word document, Excel workbook or PowerPoint presentation.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for an undefined member.</exception>
    public static string ToWire(ArchiveMediaType type) => type switch
    {
        ArchiveMediaType.TextPlain => "text/plain",
        ArchiveMediaType.TextMarkdown => "text/markdown",
        ArchiveMediaType.Pdf => "application/pdf",
        ArchiveMediaType.Png => "image/png",
        ArchiveMediaType.Jpeg => "image/jpeg",
        ArchiveMediaType.Gif => "image/gif",
        ArchiveMediaType.Docx => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ArchiveMediaType.Xlsx => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ArchiveMediaType.Pptx => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>Returns the extension an item of this type is stored with, without the dot.</summary>
    /// <param name="type">The media type.</param>
    /// <returns><c>txt</c>, <c>pdf</c>, <c>png</c>, <c>jpg</c>, <c>gif</c>, <c>docx</c>, <c>xlsx</c> or <c>pptx</c>; Markdown text is stored as <c>txt</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for an undefined member.</exception>
    public static string Extension(ArchiveMediaType type) => type switch
    {
        ArchiveMediaType.TextPlain or ArchiveMediaType.TextMarkdown => "txt",
        ArchiveMediaType.Pdf => "pdf",
        ArchiveMediaType.Png => "png",
        ArchiveMediaType.Jpeg => "jpg",
        ArchiveMediaType.Gif => "gif",
        ArchiveMediaType.Docx => "docx",
        ArchiveMediaType.Xlsx => "xlsx",
        ArchiveMediaType.Pptx => "pptx",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>
    /// Returns what the index line prints as the item's type (spec 39, owner decision OQ-4 (b)): the media-type string for the six 37
    /// types — their lines stay byte-identical — and a short label for an Office document. Every label is at most 15 characters, the
    /// length of <c>application/pdf</c>, which <c>ArchiveIndexLine.Longest</c> composes.
    /// </summary>
    /// <param name="type">The media type.</param>
    /// <returns><see cref="ToWire"/> for the 37 types; <c>Word document</c>, <c>Excel workbook</c> or <c>PowerPoint deck</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for an undefined member.</exception>
    public static string Label(ArchiveMediaType type) => type switch
    {
        ArchiveMediaType.Docx => "Word document",
        ArchiveMediaType.Xlsx => "Excel workbook",
        ArchiveMediaType.Pptx => "PowerPoint deck",
        _ => ToWire(type),
    };

    /// <summary>Maps a media-type string to a member.</summary>
    /// <param name="wire">The media-type string.</param>
    /// <param name="type">The member when recognised.</param>
    /// <returns><see langword="true"/> for exactly one of the nine strings <see cref="ToWire"/> produces.</returns>
    public static bool TryFromWire(string? wire, out ArchiveMediaType type)
    {
        foreach (var member in Members)
        {
            if (string.Equals(ToWire(member), wire, StringComparison.Ordinal))
            {
                type = member;
                return true;
            }
        }

        type = default;
        return false;
    }

    /// <summary>
    /// Maps a stored extension (without the dot, lowercase) to the member it denotes — the one extension→type list (spec 39 AC-1).
    /// <c>txt</c> reads as <see cref="ArchiveMediaType.TextPlain"/>: an orphan item has no sidecar to say it was Markdown.
    /// </summary>
    /// <param name="extension">The extension.</param>
    /// <param name="type">The member when recognised.</param>
    /// <returns><see langword="true"/> for exactly <c>txt</c>, <c>pdf</c>, <c>png</c>, <c>jpg</c>, <c>gif</c>, <c>docx</c>, <c>xlsx</c> or <c>pptx</c>.</returns>
    public static bool TryFromExtension(string? extension, out ArchiveMediaType type)
    {
        switch (extension)
        {
            case "txt":
                type = ArchiveMediaType.TextPlain;
                return true;
            case "pdf":
                type = ArchiveMediaType.Pdf;
                return true;
            case "png":
                type = ArchiveMediaType.Png;
                return true;
            case "jpg":
                type = ArchiveMediaType.Jpeg;
                return true;
            case "gif":
                type = ArchiveMediaType.Gif;
                return true;
            case "docx":
                type = ArchiveMediaType.Docx;
                return true;
            case "xlsx":
                type = ArchiveMediaType.Xlsx;
                return true;
            case "pptx":
                type = ArchiveMediaType.Pptx;
                return true;
            default:
                type = default;
                return false;
        }
    }

    /// <summary>Tells whether <paramref name="extension"/> (without the dot, lowercase) is one an archived item may carry.</summary>
    /// <param name="extension">The extension.</param>
    /// <returns><see langword="true"/> for exactly the extensions <see cref="TryFromExtension"/> recognises.</returns>
    public static bool IsItemExtension(string? extension) => TryFromExtension(extension, out _);
}
