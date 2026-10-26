using Zyggy.Core.Secrets;

namespace Zyggy.Core.Tests.Secrets;

/// <summary>Spec 09 AC-2: "the store cannot be opened" is its own exception, distinct from absence, and never carries a byte of a secret.</summary>
public sealed class SecretStoreUnavailableExceptionTests
{
    [Fact]
    public void Message_IsStoreNameColonDetail()
    {
        // Act
        var exception = new SecretStoreUnavailableException("credential-manager", "no logon session (1312)");

        // Assert
        exception.Message.Should().Be("credential-manager: no logon session (1312)");
    }

    [Fact]
    public void Properties_RoundTrip()
    {
        // Act
        var exception = new SecretStoreUnavailableException("file", "root is not a directory");

        // Assert
        exception.StoreName.Should().Be("file");
        exception.Detail.Should().Be("root is not a directory");
    }
}
