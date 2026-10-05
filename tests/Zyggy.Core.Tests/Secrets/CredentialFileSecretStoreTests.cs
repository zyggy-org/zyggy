using System.Runtime.Versioning;
using System.Text;

using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Tests.Secrets;

/// <summary>
/// The read-only credential store over Central's key files (spec 33 AC-4): <c>zy_m365_read_key</c>'s location order, checks and texts
/// (bats "token reads $CREDENTIALS_DIRECTORY…").
/// </summary>
public sealed class CredentialFileSecretStoreTests : IDisposable
{
    private static readonly TenantId Acme = TenantId.Parse("acme");
    private static readonly SecretName AppKey = SecretName.Parse("m365-app-key");
    private static readonly SecretName NewAppKey = SecretName.Parse("m365-app-key/new");
    private const string Pem = "-----BEGIN PRIVATE KEY-----\nMIIfake\n-----END PRIVATE KEY-----\n";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "zyggy-ut", Guid.NewGuid().ToString("N"));

    public CredentialFileSecretStoreTests()
    {
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(CredentialsDirectory);
    }

    public static bool IsLinux => OperatingSystem.IsLinux();

    private string ConfigDirectory => Path.Combine(_root, "config", "zyggy");

    private string CredentialsDirectory => Path.Combine(_root, "credentials");

    private string KeyFile => Path.Combine(ConfigDirectory, "m365-app.key");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Read_CredentialsDirectoryFirst_SourceCredentialsDirectory()
    {
        // Arrange
        Write(KeyFile, Pem);
        Write(Path.Combine(CredentialsDirectory, "m365-app-key"), Pem.Replace("fake", "cred", StringComparison.Ordinal));

        // Act
        var read = await Store(withCredentials: true).ReadAsync(Acme, AppKey, CancellationToken.None);

        // Assert
        read.Refusal.Should().BeNull();
        read.Source.Should().Be(CredentialSource.CredentialsDirectory);
        Encoding.ASCII.GetString(read.Value!).Should().Contain("MIIcred");
    }

    [Fact]
    public async Task Read_CredentialsDirectorySetButEmpty_KeyFile()
    {
        // Arrange
        Write(KeyFile, Pem);

        // Act
        var read = await Store(withCredentials: true).ReadAsync(Acme, AppKey, CancellationToken.None);

        // Assert
        read.Source.Should().Be(CredentialSource.File);
    }

    [Fact]
    public async Task Read_NoCredentialsDirectory_KeyFile_SourceFile()
    {
        // Arrange
        Write(KeyFile, Pem);

        // Act
        var read = await Store().ReadAsync(Acme, AppKey, CancellationToken.None);

        // Assert
        read.Source.Should().Be(CredentialSource.File);
        read.Path.Should().Be(KeyFile);
    }

    [Fact]
    public async Task Read_NewName_ReadsDotNewFile()
    {
        // Arrange
        Write(KeyFile, Pem);
        Write(KeyFile + ".new", Pem.Replace("fake", "next", StringComparison.Ordinal));

        // Act
        var read = await Store(withCredentials: true).ReadAsync(Acme, NewAppKey, CancellationToken.None);

        // Assert
        read.Source.Should().Be(CredentialSource.File);
        Encoding.ASCII.GetString(read.Value!).Should().Contain("MIInext");
    }

    [Fact]
    public async Task Read_NewNameMissing_NotARegularFile()
    {
        // Act
        var read = await Store().ReadAsync(Acme, NewAppKey, CancellationToken.None);

        // Assert
        read.Refusal.Should().Be($"key: {KeyFile}.new is not a regular file");
    }

    [Fact]
    public async Task Read_Absent_NotFoundRefusal_GetReturnsNull()
    {
        // Act
        var read = await Store().ReadAsync(Acme, AppKey, CancellationToken.None);
        var get = await Store().GetAsync(Acme, AppKey, CancellationToken.None);

        // Assert
        read.Refusal.Should().Be("key: not found in credentials directory or file");
        get.Should().BeNull();
    }

    [Fact]
    public async Task Read_Directory_NotRegularFile()
    {
        // Arrange
        Directory.CreateDirectory(KeyFile);

        // Act
        var read = await Store().ReadAsync(Acme, AppKey, CancellationToken.None);

        // Assert
        read.Refusal.Should().Be($"key: {KeyFile} is not a regular file");
    }

    [Fact]
    public async Task Read_Empty_Refused()
    {
        // Arrange
        Write(KeyFile, string.Empty);

        // Act
        var read = await Store().ReadAsync(Acme, AppKey, CancellationToken.None);

        // Assert
        read.Refusal.Should().Be($"key: {KeyFile} is empty");
    }

    [Theory]
    [InlineData("not a key\n")]
    [InlineData("-----BEGIN CERTIFICATE-----\nMII\n")]
    [InlineData("-----BEGIN PRIVATE KEY----- \n")]
    public async Task Read_NoPemHeader_Refused(string content)
    {
        // Arrange
        Write(KeyFile, content);

        // Act
        var read = await Store().ReadAsync(Acme, AppKey, CancellationToken.None);

        // Assert
        read.Refusal.Should().Be($"key: {KeyFile} has no PEM private-key header");
    }

    [Theory]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nMII\n")]
    [InlineData("-----BEGIN EC PRIVATE KEY-----\nMII\n")]
    [InlineData("comment\n-----BEGIN PRIVATE KEY-----\nMII\n")]
    public async Task Read_PemHeaderForms_Accepted(string content)
    {
        // Arrange
        Write(KeyFile, content);

        // Act
        var read = await Store().ReadAsync(Acme, AppKey, CancellationToken.None);

        // Assert
        read.Refusal.Should().BeNull();
    }

    [Fact]
    public async Task Get_OtherTenant_Refused()
    {
        // Arrange
        Write(KeyFile, Pem);

        // Act
        var act = () => Store().GetAsync(TenantId.Parse("globex"), AppKey, CancellationToken.None);

        // Assert
        (await act.Should().ThrowAsync<CredentialRefusedException>()).Which.Message.Should().Be("key: tenant globex is not this instance's");
    }

    [Fact]
    public async Task Get_Refusal_ThrowsWithShellText()
    {
        // Arrange
        Write(KeyFile, string.Empty);

        // Act
        var act = () => Store().GetAsync(Acme, AppKey, CancellationToken.None);

        // Assert
        (await act.Should().ThrowAsync<CredentialRefusedException>()).Which.Message.Should().Be($"key: {KeyFile} is empty");
    }

    [Fact]
    public async Task SetAndRemove_NotSupported()
    {
        // Act
        var set = () => Store().SetAsync(Acme, AppKey, new byte[] { 1 }, CancellationToken.None);
        var remove = () => Store().RemoveAsync(Acme, AppKey, CancellationToken.None);

        // Assert
        await set.Should().ThrowAsync<NotSupportedException>();
        await remove.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task Read_NeverCaches_RotatedFileSeenNextRead()
    {
        // Arrange
        var store = Store();
        Write(KeyFile, Pem);
        var first = await store.ReadAsync(Acme, AppKey, CancellationToken.None);

        // Act
        Write(KeyFile, Pem.Replace("fake", "rotated", StringComparison.Ordinal));
        var second = await store.ReadAsync(Acme, AppKey, CancellationToken.None);

        // Assert
        Encoding.ASCII.GetString(first.Value!).Should().Contain("MIIfake");
        Encoding.ASCII.GetString(second.Value!).Should().Contain("MIIrotated");
    }

    [Fact]
    public async Task Refusal_MessageNeverContainsKeyBytes()
    {
        // Arrange
        Write(KeyFile, "MIIsecretmaterial\nno header\n");

        // Act
        var read = await Store().ReadAsync(Acme, AppKey, CancellationToken.None);

        // Assert
        read.Refusal.Should().NotContain("MIIsecretmaterial");
        read.Value.Should().BeNull();
    }

    [Theory(SkipUnless = nameof(IsLinux), Skip = "Unix file modes: Linux only")]
    [InlineData(false, "644", false)]
    [InlineData(false, "400", false)]
    [InlineData(false, "640", false)]
    [InlineData(false, "440", false)]
    [InlineData(true, "400", true)]
    [InlineData(true, "440", true)]
    [InlineData(true, "600", true)]
    [InlineData(true, "640", false)]
    [InlineData(true, "644", false)]
    [SupportedOSPlatform("linux")]
    public async Task Read_OnLinux_Mode(bool credentialsDirectory, string mode, bool accepted)
    {
        // Arrange
        var path = credentialsDirectory ? Path.Combine(CredentialsDirectory, "m365-app-key") : KeyFile;
        File.WriteAllText(path, Pem);
        File.SetUnixFileMode(path, (UnixFileMode)Convert.ToInt32(mode, 8));

        // Act
        var read = await LinuxStore(credentialsDirectory).ReadAsync(Acme, AppKey, CancellationToken.None);

        // Assert
        if (accepted)
        {
            read.Refusal.Should().BeNull();
        }
        else
        {
            read.Refusal.Should().Be($"key: {path} must be mode 0600 (is {mode})");
        }
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "Unix owner: Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task Read_OnLinux_OwnedByThisUser_Accepted()
    {
        // Arrange
        File.WriteAllText(KeyFile, Pem);
        File.SetUnixFileMode(KeyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        // Act
        var read = await LinuxStore(false).ReadAsync(Acme, AppKey, CancellationToken.None);

        // Assert
        read.Refusal.Should().BeNull();
        read.Source.Should().Be(CredentialSource.File);
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private CredentialFileSecretStore Store(bool withCredentials = false) =>
        new(Acme, Env(withCredentials), KeyFile, checkOwnership: false);

    private CredentialFileSecretStore LinuxStore(bool withCredentials) => new(Acme, Env(withCredentials), KeyFile);

    private Dictionary<string, string?> Env(bool withCredentials) =>
        new(StringComparer.Ordinal) { ["CREDENTIALS_DIRECTORY"] = withCredentials ? CredentialsDirectory : null };
}
