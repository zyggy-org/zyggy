using System.ComponentModel;
using System.Runtime.Versioning;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using Zyggy.Core.Secrets;
using Zyggy.Core.Secrets.Windows;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Secrets;

/// <summary>
/// Spec 09 AC-1, the Fake half: the laptops' store over a substituted vault — target, user name, hex value and comment on write;
/// the same bytes back; absence as <see langword="null"/>; a vault failure as "unavailable" with the Win32 code and never the value.
/// Runs on every OS: the vault is the seam, the Credential Manager is never touched here.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialManagerSecretStoreTests
{
    private const string Target = "zyggy/acme/hmac/1";
    private const string Comment = "zyggy secret store — managed by zyggy secret";

    private static readonly TenantId Acme = TenantId.Parse("acme");
    private static readonly SecretName Hmac1 = SecretName.Parse("hmac/1");

    private readonly ICredentialVault _vault = Substitute.For<ICredentialVault>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Hex1 => Convert.ToHexStringLower(TestKeys.Secret1);

    [Fact]
    public async Task SetAsync_32Bytes_WritesTargetUserHexAndComment()
    {
        // Act
        await Store().SetAsync(Acme, Hmac1, TestKeys.Secret1, Ct);

        // Assert
        _vault.Received(1).Write(Target, "zyggy", Hex1, Comment);
    }

    [Fact]
    public async Task GetAsync_StoredHex_DecodesToSameBytes()
    {
        // Arrange
        _vault.Read(Target).Returns(Hex1);

        // Act
        ReadOnlyMemory<byte>? value = await Store().GetAsync(Acme, Hmac1, Ct);

        // Assert
        value!.Value.ToArray().Should().Equal(TestKeys.Secret1);
    }

    [Fact]
    public async Task GetAsync_AbsentTarget_NullWithoutException()
    {
        // Arrange
        _vault.Read(Target).Returns((string?)null);

        // Act
        ReadOnlyMemory<byte>? value = await Store().GetAsync(Acme, Hmac1, Ct);

        // Assert
        value.Should().BeNull();
    }

    [Fact]
    public async Task RemoveAsync_PresentThenAbsent_TrueThenFalse()
    {
        // Arrange
        _vault.TryDelete(Target).Returns(true, false);

        // Act
        bool first = await Store().RemoveAsync(Acme, Hmac1, Ct);
        bool second = await Store().RemoveAsync(Acme, Hmac1, Ct);

        // Assert
        first.Should().BeTrue();
        second.Should().BeFalse();
    }

    [Fact]
    public async Task ListAsync_FilterIsTenantPrefixWithSlashStar_TenantRelativeOrdinal()
    {
        // Arrange
        _vault.Enumerate("zyggy/acme/*").Returns(["zyggy/acme/hmac/2", "zyggy/acme/github-pat", "zyggy/acme/hmac/1"]);

        // Act
        IReadOnlyList<SecretName> names = await Store().ListAsync(Acme, Ct);

        // Assert
        names.Select(n => n.Value).Should().Equal("github-pat", "hmac/1", "hmac/2");
    }

    [Fact]
    public async Task ListAsync_NeverReturnsAnotherTenant()
    {
        // Arrange: a vault that ignores the filter — the store filters by prefix again
        _vault.Enumerate(Arg.Any<string>()).Returns(["zyggy/acme/hmac/1", "zyggy/other-org/hmac/1", "zyggy/acme-2/hmac/1", "zyggy/acme/not a name!"]);

        // Act
        IReadOnlyList<SecretName> names = await Store().ListAsync(Acme, Ct);

        // Assert
        names.Select(n => n.Value).Should().Equal("hmac/1");
    }

    [Fact]
    public async Task GetAsync_VaultThrowsWin32_UnavailableWithCodeAndTextNeverValue()
    {
        // Arrange: ERROR_NO_SUCH_LOGON_SESSION
        _vault.Read(Target).Throws(new Win32Exception(1312));

        // Act
        Func<Task> act = () => Store().GetAsync(Acme, Hmac1, Ct);

        // Assert
        var exception = (await act.Should().ThrowAsync<SecretStoreUnavailableException>()).Which;
        exception.StoreName.Should().Be("credential-manager");
        exception.Message.Should().StartWith("credential-manager: 1312 ");
        SecretSweep.AssertAbsent([exception.Message]);
    }

    [Fact]
    public async Task SetAsync_VaultThrowsWin32_Unavailable()
    {
        // Arrange
        _vault.When(v => v.Write(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())).Throw(new Win32Exception(5));

        // Act
        Func<Task> act = () => Store().SetAsync(Acme, Hmac1, TestKeys.Secret1, Ct);

        // Assert
        var exception = (await act.Should().ThrowAsync<SecretStoreUnavailableException>()).Which;
        exception.Message.Should().StartWith("credential-manager: 5 ");
        SecretSweep.AssertAbsent([exception.Message]);
    }

    [Fact]
    public async Task GetAsync_NonHexPassword_InvalidDataExceptionNamingTarget()
    {
        // Arrange
        _vault.Read(Target).Returns("not-hex-zyggy-golden");

        // Act
        Func<Task> act = () => Store().GetAsync(Acme, Hmac1, Ct);

        // Assert
        string message = (await act.Should().ThrowAsync<InvalidDataException>()).Which.Message;
        message.Should().Contain(Target).And.NotContain("not-hex-zyggy-golden");
    }

    [Fact]
    public async Task SetAsync_Empty_ArgumentException()
    {
        // Act
        Func<Task> act = () => Store().SetAsync(Acme, Hmac1, ReadOnlyMemory<byte>.Empty, Ct);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>();
        _vault.DidNotReceiveWithAnyArgs().Write(default!, default!, default!, default!);
    }

    [Fact]
    public async Task SetAsync_513Bytes_ArgumentException()
    {
        // Act
        Func<Task> act = () => Store().SetAsync(Acme, Hmac1, new byte[513], Ct);

        // Assert
        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("512 bytes");
        _vault.DidNotReceiveWithAnyArgs().Write(default!, default!, default!, default!);
    }

    [Fact]
    public async Task SetAsync_512Bytes_Accepted()
    {
        // Act
        await Store().SetAsync(Acme, Hmac1, new byte[512], Ct);

        // Assert
        _vault.Received(1).Write(Target, "zyggy", new string('0', 1024), Comment);
    }

    [Fact]
    public void Constructor_DoesNoIo()
    {
        // Act
        WindowsCredentialManagerSecretStore store = Store();

        // Assert
        store.StoreName.Should().Be("credential-manager");
        _vault.ReceivedCalls().Should().BeEmpty();
    }

    private WindowsCredentialManagerSecretStore Store() => new(_vault);
}
