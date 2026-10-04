using System.Security.Cryptography;
using System.Text;

namespace Zyggy.Core.Dream;

/// <summary>
/// The ledger's identity of a body line: the first 16 lowercase hex characters of SHA-256 over the UTF-8 line with trailing
/// whitespace removed (spec 28 <c>.dream/ledger.json</c>). Identical lines in one file are one fact.
/// </summary>
internal static class LineHash
{
    public static string Of(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(line.TrimEnd()));
        return Convert.ToHexStringLower(digest)[..16];
    }
}
