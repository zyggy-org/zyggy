using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Envelope;
using Zyggy.Core.Secrets;

namespace Zyggy.Core.Tests.Envelope;

public sealed class EnvelopeSigningRegistrationTests
{
    [Fact]
    public void AddFileSecretStoreThenAddEnvelopeSigning_ResolvesFileStoreAndSigner()
    {
        // Arrange
        string root = Path.Combine(Path.GetTempPath(), "zyggy-unit", Guid.NewGuid().ToString("N"));
        using ServiceProvider provider = new ServiceCollection()
            .AddFileSecretStore(o => o.RootDirectory = root)
            .AddEnvelopeSigning()
            .BuildServiceProvider();

        // Act
        EnvelopeSigner signer = provider.GetRequiredService<EnvelopeSigner>();

        // Assert
        signer.Should().BeSameAs(provider.GetRequiredService<EnvelopeSigner>());
        provider.GetRequiredService<ISecretStore>().GetType().Name.Should().Be("FileSecretStore");
    }

    [Fact]
    public void AddEnvelopeSigning_WithoutSecretStore_ResolvingSignerThrowsInvalidOperationException()
    {
        // Arrange
        using ServiceProvider provider = new ServiceCollection().AddEnvelopeSigning().BuildServiceProvider();
        Action act = () => provider.GetRequiredService<EnvelopeSigner>();

        // Act & Assert
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void AddEnvelopeSigning_ReturnsSameCollection()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        IServiceCollection returned = services.AddEnvelopeSigning();

        // Assert
        returned.Should().BeSameAs(services);
    }
}
