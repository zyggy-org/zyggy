using System.Text;

using Zyggy.Core.Memory;

namespace Zyggy.Core.Media;

/// <summary>
/// Recognises a file's kind from its first bytes, never from its name: the PNG, GIF and JPEG signatures (plan 36b, shared with the
/// LinkedIn image loader), the PDF header and strict UTF-8 text (spec 37 AC-8).
/// </summary>
internal static class MediaSniffer
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Returns <c>image/png</c>, <c>image/gif</c> or <c>image/jpeg</c> when the bytes start with that format's signature.</summary>
    public static string? ImageType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 24 && bytes[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return "image/png";
        }

        if (bytes.Length >= 10 && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8)))
        {
            return "image/gif";
        }

        return bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF ? "image/jpeg" : null;
    }

    /// <summary>Tells whether the bytes start with the PDF header <c>%PDF-</c>.</summary>
    public static bool IsPdf(ReadOnlySpan<byte> bytes) => bytes.Length >= 5 && bytes[..5].SequenceEqual("%PDF-"u8);

    /// <summary>
    /// Tells whether the bytes are non-empty, valid UTF-8 (a leading byte-order mark allowed) with no NUL and no C0 control character
    /// other than tab, line feed and carriage return.
    /// </summary>
    public static bool IsUtf8Text(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[..3].SequenceEqual((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            bytes = bytes[3..];
        }

        if (bytes.IsEmpty)
        {
            return false;
        }

        foreach (var b in bytes)
        {
            if (b < 0x20 && b is not ((byte)'\t' or (byte)'\n' or (byte)'\r'))
            {
                return false;
            }
        }

        try
        {
            _ = StrictUtf8.GetCharCount(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>
    /// Returns the media type of the bytes (spec 39 AC-5 order): an image type, <c>application/pdf</c>, an Office Open XML type or the
    /// Office refusal detail for a ZIP, the detail for an OLE compound file, <c>text/markdown</c> when they are text and
    /// <paramref name="markdownName"/> is set, <c>text/plain</c> for other text, or neither.
    /// </summary>
    public static MediaSniff Sniff(byte[] bytes, bool markdownName)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (ImageType(bytes) is { } image)
        {
            return new MediaSniff(image, null);
        }

        if (IsPdf(bytes))
        {
            return new MediaSniff("application/pdf", null);
        }

        if (OfficeSniffer.IsZip(bytes))
        {
            return Office(OfficeSniffer.SniffZip(bytes));
        }

        if (OfficeSniffer.IsCompoundFile(bytes))
        {
            return Office(OfficeSniffer.SniffCompoundFile(bytes));
        }

        return IsUtf8Text(bytes) ? new MediaSniff(markdownName ? "text/markdown" : "text/plain", null) : default;
    }

    private static MediaSniff Office(OfficeSniff sniff) => sniff.Kind switch
    {
        OfficeKind.Docx => new MediaSniff(ArchiveMediaTypeWire.ToWire(ArchiveMediaType.Docx), null),
        OfficeKind.Xlsx => new MediaSniff(ArchiveMediaTypeWire.ToWire(ArchiveMediaType.Xlsx), null),
        OfficeKind.Pptx => new MediaSniff(ArchiveMediaTypeWire.ToWire(ArchiveMediaType.Pptx), null),
        _ => new MediaSniff(null, sniff.Detail),
    };
}

/// <summary>What <see cref="MediaSniffer.Sniff"/> found: a media-type string, or why an Office-shaped file was refused, or neither.</summary>
internal readonly record struct MediaSniff(string? MediaType, OfficeRefusalDetail? OfficeRefusal);
