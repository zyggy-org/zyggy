using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Tests.Secrets;

public sealed class InMemorySecretStoreTests
{
    private static readonly TenantId Acme = TenantId.Parse("acme");
    private static readonly TenantId Globex = TenantId.Parse("globex");
    private static readonly SecretName Hmac1 = SecretName.Parse("hmac/1");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GetAsync_AfterSet_ReturnsSameBytes()
    {
        // Arrange
        var store = new InMemorySecretStore();
        await store.SetAsync(Acme, Hmac1, new byte[] { 1, 2, 3 }, Ct);

        // Act
        ReadOnlyMemory<byte>? value = await store.GetAsync(Acme, Hmac1, Ct);

        // Assert
        value!.Value.ToArray().Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task GetAsync_Absent_ReturnsNull()
    {
        // Act
        ReadOnlyMemory<byte>? value = await new InMemorySecretStore().GetAsync(Acme, Hmac1, Ct);

        // Assert
        value.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_OtherTenantSameName_ReturnsNull()
    {
        // Arrange
        var store = new InMemorySecretStore();
        await store.SetAsync(Acme, Hmac1, new byte[] { 1 }, Ct);

        // Act
        ReadOnlyMemory<byte>? value = await store.GetAsync(Globex, Hmac1, Ct);

        // Assert
        value.Should().BeNull();
    }

    [Fact]
    public async Task RemoveAsync_Present_ReturnsTrueThenGetIsNull()
    {
        // Arrange
        var store = new InMemorySecretStore();
        await store.SetAsync(Acme, Hmac1, new byte[] { 1 }, Ct);

        // Act
        bool removed = await store.RemoveAsync(Acme, Hmac1, Ct);

        // Assert
        removed.Should().BeTrue();
        (await store.GetAsync(Acme, Hmac1, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task RemoveAsync_Absent_ReturnsFalse()
    {
        // Act
        bool removed = await new InMemorySecretStore().RemoveAsync(Acme, Hmac1, Ct);

        // Assert
        removed.Should().BeFalse();
    }

    [Fact]
    public async Task SetAsync_EmptyValue_ThrowsArgumentException()
    {
        // Arrange
        Func<Task> act = () => new InMemorySecretStore().SetAsync(Acme, Hmac1, ReadOnlyMemory<byte>.Empty, Ct);

        // Act & Assert
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task SetAsync_Twice_Overwrites()
    {
        // Arrange
        var store = new InMemorySecretStore();
        await store.SetAsync(Acme, Hmac1, new byte[] { 1 }, Ct);

        // Act
        await store.SetAsync(Acme, Hmac1, new byte[] { 2 }, Ct);

        // Assert
        (await store.GetAsync(Acme, Hmac1, Ct))!.Value.ToArray().Should().Equal(2);
    }

    [Fact]
    public async Task SetAsync_CallerMutatesBufferAfterwards_StoredValueUnchanged()
    {
        // Arrange
        var store = new InMemorySecretStore();
        byte[] buffer = [1, 2];
        await store.SetAsync(Acme, Hmac1, buffer, Ct);

        // Act
        buffer[0] = 9;

        // Assert
        (await store.GetAsync(Acme, Hmac1, Ct))!.Value.ToArray().Should().Equal(1, 2);
    }

    [Fact]
    public async Task TwoInstances_DoNotShareState()
    {
        // Arrange
        var first = new InMemorySecretStore();
        await first.SetAsync(Acme, Hmac1, new byte[] { 1 }, Ct);

        // Act
        ReadOnlyMemory<byte>? value = await new InMemorySecretStore().GetAsync(Acme, Hmac1, Ct);

        // Assert
        value.Should().BeNull();
    }
}
