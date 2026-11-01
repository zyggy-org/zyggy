using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;

using Zyggy.Core.Memory;

namespace Zyggy.Core.Envelope;

/// <summary>
/// The body of a <c>via: bus</c> item envelope (founding spec §4, W41-6): the file as standard base64 with <c>=</c> padding, in lines of
/// <see cref="LineLength"/> characters except the last (1–76), each ended by <c>\n</c>, nothing else. Framework base64 only.
/// </summary>
public static class ItemBody
{
    /// <summary>The length of every body line but the last, in characters.</summary>
    public const int LineLength = 76;

    private static readonly string LengthRule = "must not be longer than the base64 form of the "
        + ArchiveOptions.ItemCeiling.ToString(CultureInfo.InvariantCulture) + "-byte item ceiling";

    /// <summary>Encodes a file as an item body.</summary>
    /// <param name="file">The file bytes.</param>
    /// <returns>The ASCII body bytes; empty for an empty file.</returns>
    public static byte[] Encode(ReadOnlySpan<byte> file)
    {
        string base64 = Convert.ToBase64String(file);
        int lines = (base64.Length + LineLength - 1) / LineLength;
        byte[] body = new byte[base64.Length + lines];
        int at = 0;
        for (int start = 0; start < base64.Length; start += LineLength)
        {
            int length = Math.Min(LineLength, base64.Length - start);
            for (int i = 0; i < length; i++)
            {
                body[at++] = (byte)base64[start + i];
            }

            body[at++] = (byte)'\n';
        }

        return body;
    }

    /// <summary>Returns the length of the body <see cref="Encode"/> produces for a file of <paramref name="maxBytes"/> bytes.</summary>
    /// <param name="maxBytes">The file size in bytes.</param>
    /// <returns><c>4·⌈n/3⌉ + ⌈4·⌈n/3⌉ / 76⌉</c>: the base64 length plus one line feed per line.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maxBytes"/> is negative.</exception>
    public static long MaxBodyLength(long maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        long base64 = 4 * ((maxBytes + 2) / 3);
        return base64 + ((base64 + LineLength - 1) / LineLength);
    }

    /// <summary>Decodes the body of a parsed <c>via: bus</c> item and checks it against the signed <c>size</c> and <c>sha256</c>.</summary>
    /// <param name="item">The envelope.</param>
    /// <param name="bytes">The file bytes when the body is valid; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> for a bus item whose body follows the body rule and matches its size and digest.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="item"/> is null.</exception>
    public static bool TryDecode(Envelope item, [NotNullWhen(true)] out byte[]? bytes)
    {
        ArgumentNullException.ThrowIfNull(item);
        bytes = null;
        if (item.Item is not { Via: ItemRoute.Bus } fields || Check(item.Body.Span, fields.Size, fields.Sha256, out byte[]? decoded) is not null)
        {
            return false;
        }

        bytes = decoded!;
        return true;
    }

    /// <summary>
    /// Checks a bus body in the order of plan 41 A2 — length bound, alphabet, line shape, decoded length, digest — and returns the rule
    /// of the first fault, or <see langword="null"/> with the decoded bytes. The rule never contains a byte of the body.
    /// </summary>
    internal static string? Check(ReadOnlySpan<byte> body, long size, string sha256, out byte[]? decoded)
    {
        decoded = null;
        if (body.Length > MaxBodyLength(ArchiveOptions.ItemCeiling))
        {
            return LengthRule;
        }

        foreach (byte b in body)
        {
            if (!IsBodyByte(b))
            {
                return "must hold only base64 characters, '=' and line feeds";
            }
        }

        const string ShapeRule = "must be base64 lines of 76 characters, the last 1 to 76, each ended by a line feed";
        if (body.IsEmpty || body[^1] != (byte)'\n')
        {
            return ShapeRule;
        }

        char[] chars = new char[body.Length];
        int count = 0;
        int lineStart = 0;
        for (int i = 0; i < body.Length; i++)
        {
            if (body[i] != (byte)'\n')
            {
                chars[count++] = (char)body[i];
                continue;
            }

            int length = i - lineStart;
            bool last = i == body.Length - 1;
            if (length == 0 || length > LineLength || (!last && length != LineLength))
            {
                return ShapeRule;
            }

            lineStart = i + 1;
        }

        byte[] buffer = new byte[count / 4 * 3];
        if (count % 4 != 0 || !Convert.TryFromBase64Chars(chars.AsSpan(0, count), buffer, out int written))
        {
            return ShapeRule;
        }

        if (written != size)
        {
            return "must decode to exactly size bytes";
        }

        byte[] file = buffer.AsSpan(0, written).ToArray();
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(file)), sha256, StringComparison.Ordinal))
        {
            return "must decode to bytes whose SHA-256 is sha256";
        }

        decoded = file;
        return null;
    }

    private static bool IsBodyByte(byte b) => b is (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'a' and <= (byte)'z') or (>= (byte)'0' and <= (byte)'9')
        or (byte)'+' or (byte)'/' or (byte)'=' or (byte)'\n';
}
