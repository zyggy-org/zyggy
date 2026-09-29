using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;

namespace Zyggy.Integration.Secrets;

public sealed class FileSecretStoreTests : IDisposable
{
    private static readonly TenantId Acme = TenantId.Parse("acme");
    private static readonly TenantId Globex = TenantId.Parse("globex");
    private static readonly SecretName Hmac1 = SecretName.Parse("hmac/1");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "zyggy-it", "secrets-" + Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider _provider;
    private readonly ISecretStore _store;

    public FileSecretStoreTests()
    {
        _provider = new ServiceCollection().AddFileSecretStore(o => o.RootDirectory = _root).BuildServiceProvider();
        _store = _provider.GetRequiredService<ISecretStore>();
    }

    public static bool IsLinux => OperatingSystem.IsLinux();

    public static bool IsWindows => OperatingSystem.IsWindows();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Key => [.. Enumerable.Range(1, 32).Select(i => (byte)i)];

    private string KeyFile => Path.Combine(_root, "acme", "hmac", "1");

    public void Dispose()
    {
        _provider.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task GetAsync_AfterSet_ReturnsSameBytes()
    {
        // Arrange
        await _store.SetAsync(Acme, Hmac1, Key, Ct);

        // Act
        ReadOnlyMemory<byte>? value = await _store.GetAsync(Acme, Hmac1, Ct);

        // Assert
        value!.Value.ToArray().Should().Equal(Key);
    }

    [Fact]
    public async Task RemoveAsync_Present_ReturnsTrueAndGetReturnsNull()
    {
        // Arrange
        await _store.SetAsync(Acme, Hmac1, Key, Ct);

        // Act
        bool removed = await _store.RemoveAsync(Acme, Hmac1, Ct);

        // Assert
        removed.Should().BeTrue();
        (await _store.GetAsync(Acme, Hmac1, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task RemoveAsync_Absent_ReturnsFalse()
    {
        // Act
        bool removed = await _store.RemoveAsync(Acme, Hmac1, Ct);

        // Assert
        removed.Should().BeFalse();
    }

    [Fact]
    public async Task SetAsync_WritesLowercaseHexLineAtTenantSlashName()
    {
        // Act
        await _store.SetAsync(Acme, Hmac1, Key, Ct);

        // Assert
        (await File.ReadAllTextAsync(KeyFile, Ct)).Should().Be(Convert.ToHexStringLower(Key) + "\n");
        Directory.GetFiles(Path.GetDirectoryName(KeyFile)!).Should().ContainSingle();
    }

    [Fact]
    public async Task GetAsync_Absent_ReturnsNullWithoutThrowing()
    {
        // Act
        ReadOnlyMemory<byte>? value = await _store.GetAsync(Acme, Hmac1, Ct);

        // Assert
        value.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_UppercaseHexWithSurroundingWhitespace_IsAccepted()
    {
        // Arrange
        WriteByHand(KeyFile, "  " + Convert.ToHexString(Key) + "\r\n");

        // Act
        ReadOnlyMemory<byte>? value = await _store.GetAsync(Acme, Hmac1, Ct);

        // Assert
        value!.Value.ToArray().Should().Equal(Key);
    }

    [Fact]
    public async Task GetAsync_NonHexContent_ThrowsInvalidDataExceptionNamingPath()
    {
        // Arrange
        WriteByHand(KeyFile, "zz-secret-looking-content\n");
        Func<Task> act = () => _store.GetAsync(Acme, Hmac1, Ct);

        // Act & Assert
        (await act.Should().ThrowAsync<InvalidDataException>())
            .Which.Message.Should().Contain(KeyFile).And.NotContain("secret-looking");
    }

    [Fact]
    public async Task GetAsync_OddLengthHex_ThrowsInvalidDataException()
    {
        // Arrange
        WriteByHand(KeyFile, "abc\n");
        Func<Task> act = () => _store.GetAsync(Acme, Hmac1, Ct);

        // Act & Assert
        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task SetAsync_OtherTenantSameName_IsIsolated()
    {
        // Arrange
        await _store.SetAsync(Acme, Hmac1, Key, Ct);

        // Act
        ReadOnlyMemory<byte>? value = await _store.GetAsync(Globex, Hmac1, Ct);

        // Assert
        value.Should().BeNull();
        File.Exists(Path.Combine(_root, "globex", "hmac", "1")).Should().BeFalse();
    }

    [Fact(Skip = "Linux only", SkipUnless = nameof(IsLinux))]
    [SupportedOSPlatform("linux")]
    public async Task SetAsync_OnLinux_FileIs0600AndDirectoriesAre0700()
    {
        // Act
        await _store.SetAsync(Acme, Hmac1, Key, Ct);

        // Assert
        const UnixFileMode Rwx = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        File.GetUnixFileMode(KeyFile).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.GetUnixFileMode(Path.Combine(_root, "acme")).Should().Be(Rwx);
        File.GetUnixFileMode(Path.Combine(_root, "acme", "hmac")).Should().Be(Rwx);
    }

    [Fact(Skip = "Linux only", SkipUnless = nameof(IsLinux))]
    [SupportedOSPlatform("linux")]
    public async Task SetAsync_OnLinuxOverwrite_KeepsMode()
    {
        // Arrange
        await _store.SetAsync(Acme, Hmac1, Key, Ct);

        // Act
        await _store.SetAsync(Acme, Hmac1, Key.Reverse().ToArray(), Ct);

        // Assert
        File.GetUnixFileMode(KeyFile).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact(Skip = "Windows only", SkipUnless = nameof(IsWindows))]
    [SupportedOSPlatform("windows")]
    public async Task SetAsync_OnWindows_DaclIsProtectedAndOwnerOnly()
    {
        // Act
        await _store.SetAsync(Acme, Hmac1, Key, Ct);

        // Assert
        AssertOwnerOnlyDacl(KeyFile);
    }

    [Fact(Skip = "Windows only", SkipUnless = nameof(IsWindows))]
    [SupportedOSPlatform("windows")]
    public async Task SetAsync_OnWindowsOverwrite_KeepsDacl()
    {
        // Arrange
        await _store.SetAsync(Acme, Hmac1, Key, Ct);

        // Act
        await _store.SetAsync(Acme, Hmac1, Key.Reverse().ToArray(), Ct);

        // Assert
        AssertOwnerOnlyDacl(KeyFile);
    }

    [Fact]
    public async Task SetAsync_EmptyValue_ThrowsArgumentException()
    {
        // Arrange
        Func<Task> act = () => _store.SetAsync(Acme, Hmac1, ReadOnlyMemory<byte>.Empty, Ct);

        // Act & Assert
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void Constructor_EmptyRootDirectory_ThrowsInvalidOperationException()
    {
        // Arrange
        using ServiceProvider provider = new ServiceCollection().AddFileSecretStore(o => o.RootDirectory = "").BuildServiceProvider();
        Action act = () => provider.GetRequiredService<ISecretStore>();

        // Act & Assert
        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("RootDirectory");
    }

    [SupportedOSPlatform("windows")]
    private static void AssertOwnerOnlyDacl(string path)
    {
        FileSecurity security = new FileInfo(path).GetAccessControl();
        security.AreAccessRulesProtected.Should().BeTrue();
        AuthorizationRuleCollection rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        rules.Count.Should().Be(1);
        FileSystemAccessRule rule = rules[0].Should().BeOfType<FileSystemAccessRule>().Subject;
        rule.IdentityReference.Should().Be(WindowsIdentity.GetCurrent().User);
        rule.FileSystemRights.Should().HaveFlag(FileSystemRights.FullControl);
        rule.AccessControlType.Should().Be(AccessControlType.Allow);
        rule.IsInherited.Should().BeFalse();
    }

    private static void WriteByHand(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
