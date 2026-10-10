using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;

using Zyggy.Core.Media;

namespace Zyggy.Core.LinkedIn;

/// <summary>An image read once for upload: its bytes, media type, dimensions and SHA-256.</summary>
internal sealed record LoadedImage(ReadOnlyMemory<byte> Bytes, string MediaType, int Width, int Height, string Sha256);

/// <summary>The outcome of <see cref="PostImage.Load"/>: the image, or the refusal reason (one <c>refused: image …</c> row).</summary>
internal sealed record PostImageLoad(LoadedImage? Image, string? Refusal);

/// <summary>
/// The local image checks of <c>publish_post</c> (plan 36b D2, D3), each before any request: the path is absolute, normalised and inside
/// <c>image.dir</c>; no component from the folder down is a symbolic link; it is a regular file within <c>image.max_bytes</c>; its first
/// bytes are a PNG, GIF or JPEG whose header dimensions give fewer than LinkedIn's 36,152,320 pixels; and the SHA-256 of exactly the bytes
/// read is the approved one. The file is read once; those bytes are what is uploaded.
/// </summary>
internal static class PostImage
{
    public const long MaxPixels = 36_152_320;

    public static PostImageLoad Load(string path, string expectedSha256, string? mediaDir, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(expectedSha256);
        if (mediaDir is null)
        {
            return Refuse("image.dir not configured");
        }

        var dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(mediaDir));
        if (!Path.IsPathFullyQualified(path)
            || !string.Equals(Path.GetFullPath(path), path, StringComparison.Ordinal)
            || !path.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return Refuse("image outside image.dir");
        }

        var file = new FileInfo(path);
        if (!file.Exists || file.LinkTarget is not null || HasLinkBetween(dir, file.Directory))
        {
            return Refuse("image not a regular file");
        }

        if (file.Length > maxBytes)
        {
            return Refuse(string.Create(CultureInfo.InvariantCulture, $"image too large ({file.Length} > {maxBytes})"));
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Refuse("image not a regular file");
        }

        if (bytes.Length > maxBytes)
        {
            return Refuse(string.Create(CultureInfo.InvariantCulture, $"image too large ({bytes.Length} > {maxBytes})"));
        }

        var mediaType = MediaSniffer.ImageType(bytes);
        if (mediaType is null)
        {
            return Refuse("image not PNG, JPEG or GIF");
        }

        if (Dimensions(mediaType, bytes) is not var (width, height) || width <= 0 || height <= 0)
        {
            return Refuse("image dimensions unreadable");
        }

        var pixels = (long)width * height;
        if (pixels >= MaxPixels)
        {
            return Refuse(string.Create(CultureInfo.InvariantCulture, $"image too many pixels ({pixels} ≥ {MaxPixels})"));
        }

        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return string.Equals(sha, expectedSha256, StringComparison.Ordinal)
            ? new PostImageLoad(new LoadedImage(bytes, mediaType, width, height, sha), null)
            : Refuse("image hash mismatch");
    }

    private static PostImageLoad Refuse(string reason) => new(null, reason);

    // Any directory from the media folder (included) down to the file's folder that is a symbolic link.
    private static bool HasLinkBetween(string dir, DirectoryInfo? current)
    {
        for (var d = current; d is not null; d = d.Parent)
        {
            if (d.LinkTarget is not null)
            {
                return true;
            }

            if (string.Equals(Path.TrimEndingDirectorySeparator(d.FullName), dir, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static (int Width, int Height)? Dimensions(string mediaType, byte[] b) => mediaType switch
    {
        "image/png" => b.AsSpan(12, 4).SequenceEqual("IHDR"u8)
            ? (BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(20)))
            : null,
        "image/gif" => (BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(6)), BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(8))),
        _ => JpegDimensions(b),
    };

    // Walks the JPEG segments to the first start-of-frame (SOF0..SOF15 except DHT C4, JPG C8, DAC CC).
    private static (int Width, int Height)? JpegDimensions(byte[] b)
    {
        var i = 2;
        while (i + 3 < b.Length)
        {
            if (b[i] != 0xFF)
            {
                return null;
            }

            var marker = b[i + 1];
            if (marker == 0xFF)
            {
                i++;
                continue;
            }

            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7))
            {
                i += 2;
                continue;
            }

            if (marker is 0xD9 or 0xDA)
            {
                return null;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(i + 2));
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                return i + 8 < b.Length
                    ? (BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(i + 7)), BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(i + 5)))
                    : null;
            }

            if (length < 2)
            {
                return null;
            }

            i += 2 + length;
        }

        return null;
    }
}
