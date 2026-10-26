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

    [Fact]
    public async Task ListAsync_TwoTenants_ReturnsOnlyThisTenantsNamesInOrdinalOrder()
    {
        // Arrange: spec 09 AC-1 — names only, this tenant only, ordinal order
        var store = new InMemorySecretStore();
        await store.SetAsync(Acme, SecretName.Parse("hmac/2"), new byte[] { 2 }, Ct);
        await store.SetAsync(Acme, SecretName.Parse("github-pat"), new byte[] { 3 }, Ct);
        await store.SetAsync(Acme, Hmac1, new byte[] { 1 }, Ct);
        await store.SetAsync(TenantId.Parse("other-org"), Hmac1, new byte[] { 9 }, Ct);

        // Act
        IReadOnlyList<SecretName> names = await store.ListAsync(Acme, Ct);

        // Assert
        names.Select(n => n.Value).Should().Equal("github-pat", "hmac/1", "hmac/2");
    }

    [Fact]
    public async Task ListAsync_UnknownTenant_Empty()
    {
        // Arrange
        var store = new InMemorySecretStore();
        await store.SetAsync(Acme, Hmac1, new byte[] { 1 }, Ct);

        // Act
        IReadOnlyList<SecretName> names = await store.ListAsync(Globex, Ct);

        // Assert
        names.Should().BeEmpty();
    }

    [Fact]
    public async Task ListAsync_AfterRemove_NameGone()
    {
        // Arrange
        var store = new InMemorySecretStore();
        await store.SetAsync(Acme, Hmac1, new byte[] { 1 }, Ct);
        await store.RemoveAsync(Acme, Hmac1, Ct);

        // Act
        IReadOnlyList<SecretName> names = await store.ListAsync(Acme, Ct);

        // Assert
        names.Should().BeEmpty();
    }
}
