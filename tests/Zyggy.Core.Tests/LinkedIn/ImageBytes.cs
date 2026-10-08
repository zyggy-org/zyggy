using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Zyggy.Core.Tests.LinkedIn;

/// <summary>The smallest PNG, GIF and JPEG headers the image loader reads (plan 36b Step 1): signature and dimensions, padded to a size.</summary>
internal static class ImageBytes
{
    public static byte[] Png(int width, int height, int pad = 32)
    {
        var bytes = new byte[33 + pad];
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20), height);
        bytes[24] = 8;
        bytes[25] = 2;
        return bytes;
    }

    public static byte[] Gif(int width, int height, int pad = 16)
    {
        var bytes = new byte[13 + pad];
        "GIF89a"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), (ushort)height);
        return bytes;
    }

    /// <summary>SOI, an APP0 segment, a baseline SOF0 with the dimensions, EOI.</summary>
    public static byte[] Jpeg(int width, int height)
    {
        using var stream = new MemoryStream();
        stream.Write([0xFF, 0xD8]);
        stream.Write([0xFF, 0xE0, 0x00, 0x10]);
        stream.Write("JFIF\0"u8);
        stream.Write(new byte[9]);
        stream.Write([0xFF, 0xC0, 0x00, 0x11, 0x08]);
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(size, (ushort)height);
        BinaryPrimitives.WriteUInt16BigEndian(size[2..], (ushort)width);
        stream.Write(size);
        stream.Write([0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01]);
        stream.Write([0xFF, 0xD9]);
        return stream.ToArray();
    }

    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
