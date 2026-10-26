using Zyggy.Core.Secrets;

namespace Zyggy.Core.Tests.Secrets;

/// <summary>Spec 09 AC-4: the closed vocabulary of <c>node.json</c>'s <c>secrets.store</c> — two words, case-sensitive like every other wire word.</summary>
public sealed class SecretStoreKindTests
{
    [Theory]
    [InlineData(SecretStoreKind.Os, "os")]
    [InlineData(SecretStoreKind.File, "file")]
    public void ToWire_EveryMember_ExactWord(SecretStoreKind kind, string wire)
    {
        SecretStoreKindWire.ToWire(kind).Should().Be(wire);
    }

    [Fact]
    public void ToWire_CoversEveryMember()
    {
        foreach (SecretStoreKind kind in Enum.GetValues<SecretStoreKind>())
        {
            SecretStoreKindWire.TryParse(SecretStoreKindWire.ToWire(kind), out SecretStoreKind back).Should().BeTrue();
            back.Should().Be(kind);
        }
    }

    [Theory]
    [InlineData("OS")]
    [InlineData("File")]
    [InlineData("vault")]
    [InlineData("credential-manager")]
    [InlineData("linux")]
    [InlineData("")]
    public void TryParse_UnknownOrWrongCase_False(string word)
    {
        SecretStoreKindWire.TryParse(word, out _).Should().BeFalse();
    }
}
