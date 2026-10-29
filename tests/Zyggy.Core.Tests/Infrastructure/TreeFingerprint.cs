using System.Security.Cryptography;

namespace Zyggy.Core.Tests.Infrastructure;

/// <summary>Every file under a directory as (relative path with <c>/</c>, SHA-256 of its bytes), sorted by path.</summary>
internal static class TreeFingerprint
{
    public static List<(string Path, string Sha256)> Of(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(file => (
                Path: System.IO.Path.GetRelativePath(directory, file).Replace('\\', '/'),
                Sha256: Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)))))
            .OrderBy(entry => entry.Path, StringComparer.Ordinal)
            .ToList();
}
