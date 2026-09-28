using Zyggy.Core.Envelope;

namespace Zyggy.Core.Tests.Envelope;

public sealed class KeyIdTests
{
    [Fact]
    public void TryParse_TenantSlashNumber_ReturnsTenantAndNumber()
    {
        // Act
        bool ok = KeyId.TryParse("acme/1", out KeyId? keyId);

        // Assert
        ok.Should().BeTrue();
        keyId!.Tenant.Value.Should().Be("acme");
        keyId.Number.Should().Be(1);
        keyId.ToString().Should().Be("acme/1");
        keyId.SecretName.Value.Should().Be("hmac/1");
    }

    [Theory]
    [InlineData("acme/0")]
    [InlineData("acme/01")]
    [InlineData("acme")]
    [InlineData("acme/1/2")]
    [InlineData("Acme/1")]
    [InlineData("acme/-1")]
    [InlineData("acme/99999999999")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_Invalid_ReturnsFalse(string? value)
    {
        // Act
        bool ok = KeyId.TryParse(value, out KeyId? keyId);

        // Assert
        ok.Should().BeFalse();
        keyId.Should().BeNull();
    }

    [Fact]
    public void Parse_Invalid_ThrowsFormatException()
    {
        // Arrange
        Action act = () => KeyId.Parse("acme/0");

        // Act & Assert
        act.Should().Throw<FormatException>();
    }
}
