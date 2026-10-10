namespace Zyggy.Core.Memory;

/// <summary>The single mapping between <see cref="ArchiveMediaType"/>, its media-type string and its stored extension.</summary>
public static class ArchiveMediaTypeWire
{
    private static readonly ArchiveMediaType[] Members = Enum.GetValues<ArchiveMediaType>();

    private static readonly string[] ItemExtensions = ["txt", "pdf", "png", "jpg", "gif"];

    /// <summary>Gets every member, in declaration order.</summary>
    public static IReadOnlyList<ArchiveMediaType> All => Members;

    /// <summary>Returns the media-type string of a member.</summary>
    /// <param name="type">The media type.</param>
    /// <returns><c>text/plain</c>, <c>text/markdown</c>, <c>application/pdf</c>, <c>image/png</c>, <c>image/jpeg</c> or <c>image/gif</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for an undefined member.</exception>
    public static string ToWire(ArchiveMediaType type) => type switch
    {
        ArchiveMediaType.TextPlain => "text/plain",
        ArchiveMediaType.TextMarkdown => "text/markdown",
        ArchiveMediaType.Pdf => "application/pdf",
        ArchiveMediaType.Png => "image/png",
        ArchiveMediaType.Jpeg => "image/jpeg",
        ArchiveMediaType.Gif => "image/gif",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>Returns the extension an item of this type is stored with, without the dot.</summary>
    /// <param name="type">The media type.</param>
    /// <returns><c>txt</c>, <c>pdf</c>, <c>png</c>, <c>jpg</c> or <c>gif</c>; Markdown text is stored as <c>txt</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for an undefined member.</exception>
    public static string Extension(ArchiveMediaType type) => type switch
    {
        ArchiveMediaType.TextPlain or ArchiveMediaType.TextMarkdown => "txt",
        ArchiveMediaType.Pdf => "pdf",
        ArchiveMediaType.Png => "png",
        ArchiveMediaType.Jpeg => "jpg",
        ArchiveMediaType.Gif => "gif",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>Maps a media-type string to a member.</summary>
    /// <param name="wire">The media-type string.</param>
    /// <param name="type">The member when recognised.</param>
    /// <returns><see langword="true"/> for exactly one of the six strings <see cref="ToWire"/> produces.</returns>
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

    /// <summary>Tells whether <paramref name="extension"/> (without the dot, lowercase) is one an archived item may carry.</summary>
    /// <param name="extension">The extension.</param>
    /// <returns><see langword="true"/> for exactly <c>txt</c>, <c>pdf</c>, <c>png</c>, <c>jpg</c> or <c>gif</c>.</returns>
    public static bool IsItemExtension(string? extension) => extension is not null && Array.IndexOf(ItemExtensions, extension) >= 0;
}
