using System.Security.Cryptography;

using Zyggy.Core.Secrets;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Secrets;

/// <summary>Spec 09 AC-1: the fingerprint an owner compares across machines — eight lowercase hex characters of SHA-256, never the bytes.</summary>
public sealed class SecretFingerprintTests
{
    [Fact]
    public void Of_KnownBytes_First8HexOfSha256()
    {
        // Arrange
        string expected = Convert.ToHexStringLower(SHA256.HashData(TestKeys.Secret1))[..8];

        // Act
        string fingerprint = SecretFingerprint.Of(TestKeys.Secret1);

        // Assert
        fingerprint.Should().Be(expected);
    }

    [Fact]
    public void Of_Lowercase_EightChars()
    {
        // Act
        string fingerprint = SecretFingerprint.Of([1, 2, 3]);

        // Assert
        fingerprint.Should().HaveLength(8).And.MatchRegex("^[0-9a-f]{8}$");
    }

    [Fact]
    public void Of_Empty_Throws()
    {
        // Act
        Action act = () => SecretFingerprint.Of(ReadOnlySpan<byte>.Empty);

        // Assert
        act.Should().Throw<ArgumentException>();
    }
}
