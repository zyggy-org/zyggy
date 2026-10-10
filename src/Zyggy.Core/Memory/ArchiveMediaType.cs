namespace Zyggy.Core.Memory;

/// <summary>
/// The closed list of media types an archived item may have (spec 37 Contracts). The type is sniffed from the item's bytes, never taken
/// from the file name; it decides the stored extension.
/// </summary>
public enum ArchiveMediaType
{
    /// <summary>UTF-8 text (<c>text/plain</c>, stored as <c>.txt</c>).</summary>
    TextPlain,

    /// <summary>UTF-8 text whose source was named <c>.md</c> or <c>.markdown</c> (<c>text/markdown</c>, stored as <c>.txt</c>: <c>.md</c> is the sidecar).</summary>
    TextMarkdown,

    /// <summary>A PDF document (<c>application/pdf</c>).</summary>
    Pdf,

    /// <summary>A PNG image (<c>image/png</c>).</summary>
    Png,

    /// <summary>A JPEG image (<c>image/jpeg</c>, stored as <c>.jpg</c>).</summary>
    Jpeg,

    /// <summary>A GIF image (<c>image/gif</c>).</summary>
    Gif,
}
