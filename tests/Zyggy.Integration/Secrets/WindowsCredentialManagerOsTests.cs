using System.Runtime.Versioning;
using System.Text;

using Meziantou.Framework.Win32;

using Zyggy.Core.Secrets;
using Zyggy.Core.Secrets.Windows;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Integration.Secrets;

/// <summary>
/// Spec 09 AC-9 against the real Credential Manager of the interactive user: the production store over the production vault
/// round-trips a key and a token under a throwaway tenant and leaves the vault clean. Skipped visibly when not interactive
/// Windows, or when the vault answers ERROR_NO_SUCH_LOGON_SESSION (1312) as it does for a hosted CI agent (spec note 4, owner 2026-10-25).
/// The one place a test names the library, to read a credential's type back.
/// </summary>
[Trait("Category", "OsStore")]
[SupportedOSPlatform("windows5.1.2600")]
public sealed class WindowsCredentialManagerOsTests
{
    private const string Skipped = "Windows Credential Manager of the interactive user: Windows only";

    private static readonly SecretName Hmac1 = SecretName.Parse("hmac/1");
    private static readonly SecretName GitHubPat = SecretName.Parse("github-pat");

    private readonly TenantId _tenant = TenantId.Parse("acme-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly MeziantouCredentialVault _vault = new();

    public static bool IsInteractiveWindows => OperatingSystem.IsWindows() && Environment.UserInteractive;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Key => [.. Enumerable.Range(1, 32).Select(i => (byte)i)];

    private static byte[] Token => Encoding.UTF8.GetBytes(TestKeys.TestPat);

    [Fact(SkipUnless = nameof(IsInteractiveWindows), Skip = Skipped)]
    public async Task RoundTrip_KeyAndToken_BytesIdenticalAndListed()
    {
        WindowsCredentialManagerSecretStore store = Store();
        try
        {
            // Arrange
            await FirstWriteOrSkipAsync(store, Hmac1, Key);
            await store.SetAsync(_tenant, GitHubPat, Token, Ct);

            // Act
            ReadOnlyMemory<byte>? key = await store.GetAsync(_tenant, Hmac1, Ct);
            ReadOnlyMemory<byte>? token = await store.GetAsync(_tenant, GitHubPat, Ct);
            IReadOnlyList<SecretName> names = await store.ListAsync(_tenant, Ct);

            // Assert
            key!.Value.ToArray().Should().Equal(Key);
            token!.Value.ToArray().Should().Equal(Token);
            names.Select(n => n.Value).Should().Equal("github-pat", "hmac/1");
            Credential? stored = CredentialManager.ReadCredential(WindowsCredentialManagerSecretStore.Target(_tenant, Hmac1));
            stored!.CredentialType.Should().Be(CredentialType.Generic);
            stored.UserName.Should().Be("zyggy");
            stored.Comment.Should().Be("zyggy secret store — managed by zyggy secret");
        }
        finally
        {
            await CleanupAsync(store);
        }
    }

    [Fact(SkipUnless = nameof(IsInteractiveWindows), Skip = Skipped)]
    public async Task Remove_ThenGet_NullAndEnumerateEmpty()
    {
        WindowsCredentialManagerSecretStore store = Store();
        try
        {
            // Arrange
            await FirstWriteOrSkipAsync(store, Hmac1, Key);

            // Act
            bool removed = await store.RemoveAsync(_tenant, Hmac1, Ct);
            bool removedAgain = await store.RemoveAsync(_tenant, Hmac1, Ct);

            // Assert
            removed.Should().BeTrue();
            removedAgain.Should().BeFalse();
            (await store.GetAsync(_tenant, Hmac1, Ct)).Should().BeNull();
            _vault.Enumerate(WindowsCredentialManagerSecretStore.Filter(_tenant)).Should().BeEmpty();
        }
        finally
        {
            await CleanupAsync(store);
        }
    }

    [Fact(SkipUnless = nameof(IsInteractiveWindows), Skip = Skipped)]
    public async Task Get_HandEditedNonHexPassword_InvalidDataException()
    {
        WindowsCredentialManagerSecretStore store = Store();
        string target = WindowsCredentialManagerSecretStore.Target(_tenant, Hmac1);
        try
        {
            // Arrange: a password written by hand, not hex
            await FirstWriteOrSkipAsync(store, Hmac1, Key);
            CredentialManager.WriteCredential(target, "zyggy", "typed-by-hand", "zyggy secret store — managed by zyggy secret", CredentialPersistence.LocalMachine);

            // Act
            Func<Task> act = () => store.GetAsync(_tenant, Hmac1, Ct);

            // Assert
            string message = (await act.Should().ThrowAsync<InvalidDataException>()).Which.Message;
            message.Should().Contain(target).And.NotContain("typed-by-hand");
        }
        finally
        {
            await CleanupAsync(store);
        }
    }

    [Fact(SkipUnless = nameof(IsInteractiveWindows), Skip = Skipped)]
    public async Task OtherTenantTarget_NeverListed()
    {
        WindowsCredentialManagerSecretStore store = Store();
        TenantId other = TenantId.Parse(_tenant.Value + "-2");
        try
        {
            // Arrange: a second tenant whose name extends the first's
            await FirstWriteOrSkipAsync(store, Hmac1, Key);
            await store.SetAsync(other, Hmac1, Key, Ct);

            // Act
            IReadOnlyList<SecretName> names = await store.ListAsync(_tenant, Ct);
            IReadOnlyList<SecretName> otherNames = await store.ListAsync(other, Ct);

            // Assert
            names.Select(n => n.Value).Should().Equal("hmac/1");
            otherNames.Select(n => n.Value).Should().Equal("hmac/1");
        }
        finally
        {
            await store.RemoveAsync(other, Hmac1, CancellationToken.None);
            await CleanupAsync(store);
            _vault.Enumerate(WindowsCredentialManagerSecretStore.Filter(other)).Should().BeEmpty();
        }
    }

    private WindowsCredentialManagerSecretStore Store() => new(_vault);

    // The first write tells a hosted agent's session apart from a real one: 1312 is a visible skip, not a failure (spec note 4).
    private async Task FirstWriteOrSkipAsync(WindowsCredentialManagerSecretStore store, SecretName name, byte[] value)
    {
        try
        {
            await store.SetAsync(_tenant, name, value, Ct);
        }
        catch (SecretStoreUnavailableException ex) when (ex.Detail.StartsWith("1312 ", StringComparison.Ordinal))
        {
            Assert.Skip("Credential Manager unavailable: ERROR_NO_SUCH_LOGON_SESSION (1312) — " + ex.Detail);
        }
    }

    private async Task CleanupAsync(WindowsCredentialManagerSecretStore store)
    {
        await store.RemoveAsync(_tenant, Hmac1, CancellationToken.None);
        await store.RemoveAsync(_tenant, GitHubPat, CancellationToken.None);
        _vault.Enumerate(WindowsCredentialManagerSecretStore.Filter(_tenant)).Should().BeEmpty("every fact leaves the vault clean");
    }
}
