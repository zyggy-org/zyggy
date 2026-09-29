using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Secrets;

namespace Zyggy.Core.Tests.Secrets;

public sealed class FileSecretStoreRegistrationTests
{
    [Fact]
    public void AddFileSecretStore_ResolvesSingletonISecretStore()
    {
        // Arrange
        string root = Path.Combine(Path.GetTempPath(), "zyggy-unit", Guid.NewGuid().ToString("N"));
        using ServiceProvider provider = new ServiceCollection().AddFileSecretStore(o => o.RootDirectory = root).BuildServiceProvider();

        // Act
        ISecretStore first = provider.GetRequiredService<ISecretStore>();
        ISecretStore second = provider.GetRequiredService<ISecretStore>();

        // Assert
        first.Should().BeSameAs(second);
        first.GetType().Name.Should().Be("FileSecretStore");
        Directory.Exists(root).Should().BeFalse("construction performs no I/O");
    }

    [Fact]
    public void AddFileSecretStore_WithoutConfigure_UsesLocalApplicationDataDefault()
    {
        // Arrange
        Assert.SkipWhen(
            string.IsNullOrEmpty(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
            "no user profile directory on this machine");
        using ServiceProvider provider = new ServiceCollection().AddFileSecretStore().BuildServiceProvider();

        // Act
        Action act = () => provider.GetRequiredService<ISecretStore>();

        // Assert
        act.Should().NotThrow();
    }
}
