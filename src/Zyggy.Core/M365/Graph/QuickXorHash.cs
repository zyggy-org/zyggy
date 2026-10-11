using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Zyggy.Core.M365.Graph;

/// <summary>
/// OneDrive's QuickXorHash (Microsoft's published algorithm; spec 38 AC-12): every input byte is XOR-ed into a 160-bit state at a bit
/// position that moves 11 bits per byte, and the input length is XOR-ed little-endian into the last 64 bits. The only content hash
/// Graph guarantees for a drive item, so the offload verifies an upload with it; no package.
/// </summary>
internal sealed class QuickXorHash : HashAlgorithm
{
    private const int WidthInBits = 160;
    private const int Shift = 11;

    private readonly ulong[] _cells = new ulong[3];
    private long _length;
    private int _shift;

    public QuickXorHash() => HashSizeValue = WidthInBits;

    /// <summary>The base64 hash of <paramref name="bytes"/>, as Graph's <c>file.hashes.quickXorHash</c> carries it.</summary>
    public static string Base64(ReadOnlySpan<byte> bytes)
    {
        using var hash = new QuickXorHash();
        var buffer = bytes.ToArray();
        return Convert.ToBase64String(hash.ComputeHash(buffer));
    }

    public override void Initialize()
    {
        Array.Clear(_cells);
        _length = 0;
        _shift = 0;
    }

    protected override void HashCore(byte[] array, int ibStart, int cbSize)
    {
        for (var i = ibStart; i < ibStart + cbSize; i++)
        {
            var cell = _shift / 64;
            var offset = _shift % 64;
            _cells[cell] ^= (ulong)array[i] << offset;

            // A byte that straddles a cell boundary carries its high bits into the next cell (the last cell is 32 bits wide).
            var cellWidth = cell == 2 ? 32 : 64;
            if (offset > cellWidth - 8)
            {
                _cells[cell == 2 ? 0 : cell + 1] ^= (ulong)array[i] >> (cellWidth - offset);
            }

            _shift = (_shift + Shift) % WidthInBits;
        }

        _length += cbSize;
    }

    protected override byte[] HashFinal()
    {
        var result = new byte[WidthInBits / 8];
        BinaryPrimitives.WriteUInt64LittleEndian(result, _cells[0]);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(8), _cells[1]);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16), (uint)_cells[2]);

        Span<byte> length = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(length, _length);
        for (var i = 0; i < 8; i++)
        {
            result[WidthInBits / 8 - 8 + i] ^= length[i];
        }

        return result;
    }
}
