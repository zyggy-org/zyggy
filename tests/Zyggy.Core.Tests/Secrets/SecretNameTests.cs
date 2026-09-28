using Zyggy.Core.Secrets;

namespace Zyggy.Core.Tests.Secrets;

public sealed class SecretNameTests
{
    public static TheoryData<string> Valid => new() { "hmac/1", "github-pat", "a/b/c", new string('a', 64) };

    public static TheoryData<string> Invalid =>
        new() { "../x", "/x", "x/", "a//b", "Hmac/1", "a.b", "", new string('a', 65), "a b" };

    [Theory]
    [MemberData(nameof(Valid))]
    public void TryParse_Valid_ReturnsTrue(string value)
    {
        // Act
        bool ok = SecretName.TryParse(value, out SecretName? name);

        // Assert
        ok.Should().BeTrue();
        name!.Value.Should().Be(value);
        name.ToString().Should().Be(value);
    }

    [Theory]
    [MemberData(nameof(Invalid))]
    public void TryParse_Invalid_ReturnsFalse(string value)
    {
        // Act
        bool ok = SecretName.TryParse(value, out SecretName? name);

        // Assert
        ok.Should().BeFalse();
        name.Should().BeNull();
    }

    [Fact]
    public void TryParse_Null_ReturnsFalse()
    {
        // Act
        bool ok = SecretName.TryParse(null, out SecretName? name);

        // Assert
        ok.Should().BeFalse();
        name.Should().BeNull();
    }
}
