using System.Text;

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
    /// Returns the media type of the bytes: an image type, <c>application/pdf</c>, <c>text/markdown</c> when they are text and
    /// <paramref name="markdownName"/> is set, <c>text/plain</c> for other text, or <see langword="null"/>.
    /// </summary>
    public static string? Sniff(ReadOnlySpan<byte> bytes, bool markdownName)
    {
        if (ImageType(bytes) is { } image)
        {
            return image;
        }

        if (IsPdf(bytes))
        {
            return "application/pdf";
        }

        return IsUtf8Text(bytes) ? (markdownName ? "text/markdown" : "text/plain") : null;
    }
}
