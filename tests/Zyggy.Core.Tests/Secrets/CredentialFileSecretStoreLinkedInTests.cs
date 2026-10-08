using System.Runtime.Versioning;
using System.Text;

using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Tests.Secrets;

/// <summary>
/// The LinkedIn names of the credential store (spec 36 AC-19): <c>linkedin/client-secret</c> read-only from the credentials directory
/// or the file, one line, no PEM check; <c>linkedin/token</c> read and written 0600 in a 0700 directory; 33's m365 names unchanged.
/// </summary>
public sealed class CredentialFileSecretStoreLinkedInTests : IDisposable
{
    private const string Pem = "-----BEGIN PRIVATE KEY-----\nMIIfake\n-----END PRIVATE KEY-----\n";
    private const string Secret = "client-secret-value-0001";

    private static readonly TenantId Acme = TenantId.Parse("acme");
    private static readonly SecretName ClientSecret = SecretName.Parse("linkedin/client-secret");
    private static readonly SecretName Token = SecretName.Parse("linkedin/token");
    private static readonly SecretName AppKey = SecretName.Parse("m365-app-key");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "zyggy-ut", Guid.NewGuid().ToString("N"));

    public CredentialFileSecretStoreLinkedInTests()
    {
        Directory.CreateDirectory(CredentialsDirectory);
    }

    public static bool IsLinux => OperatingSystem.IsLinux();

    private string LinkedInDirectory => Path.Combine(_root, "config", "zyggy", "linkedin");

    private string CredentialsDirectory => Path.Combine(_root, "credentials");

    private string KeyFile => Path.Combine(_root, "config", "zyggy", "m365-app.key");

    private string SecretFile => Path.Combine(LinkedInDirectory, "client-secret");

    private string TokenFile => Path.Combine(LinkedInDirectory, "token.json");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact(SkipUnless = nameof(IsLinux), Skip = "Unix file modes: Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task ClientSecret_CredentialsDirectoryFirst_OnLinux()
    {
        // Arrange
        Write(SecretFile, Secret + "\n", "600");
        Write(Path.Combine(CredentialsDirectory, "linkedin-client-secret"), "from-credentials\n", "440");

        // Act
        var read = await LinuxStore(withCredentials: true).ReadAsync(Acme, ClientSecret, CancellationToken.None);

        // Assert
        read.Refusal.Should().BeNull();
        read.Source.Should().Be(CredentialSource.CredentialsDirectory);
        Encoding.UTF8.GetString(read.Value!).Should().Be("from-credentials");
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "Unix file modes: Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task ClientSecret_FileFallback_0600_OnLinux()
    {
        // Arrange
        Write(SecretFile, Secret + "\n", "600");

        // Act
        var value = await LinuxStore(withCredentials: true).GetAsync(Acme, ClientSecret, CancellationToken.None);

        // Assert
        Encoding.UTF8.GetString(value!.Value.Span).Should().Be(Secret);
    }

    [Theory(SkipUnless = nameof(IsLinux), Skip = "Unix file modes: Linux only")]
    [InlineData("644", Secret + "\n", "must be mode 0600 (is 644)")]
    [InlineData("400", Secret + "\n", "must be mode 0600 (is 400)")]
    [InlineData("600", "", "is empty")]
    [InlineData("600", "\n", "is not one line")]
    [InlineData("600", Secret + "\nsecond\n", "is not one line")]
    [InlineData("600", Secret + "\r\n", "is not one line")]
    [InlineData(null, null, "is not a regular file")]
    [SupportedOSPlatform("linux")]
    public async Task ClientSecret_BadModeEmptyTwoLinesNotRegular_RefusedNamesPathNotValue_OnLinux(string? mode, string? content, string reason)
    {
        // Arrange
        if (mode is null)
        {
            Directory.CreateDirectory(SecretFile);
        }
        else
        {
            Write(SecretFile, content!, mode);
        }

        // Act
        var read = await LinuxStore().ReadAsync(Acme, ClientSecret, CancellationToken.None);

        // Assert
        read.Value.Should().BeNull();
        read.Refusal.Should().Be($"linkedin: {SecretFile} {reason}").And.NotContain(Secret);
    }

    [Fact]
    public async Task ClientSecret_NoPemCheck()
    {
        // Arrange
        Write(SecretFile, Secret, mode: null);

        // Act
        var read = await Store().ReadAsync(Acme, ClientSecret, CancellationToken.None);

        // Assert
        read.Refusal.Should().BeNull();
        Encoding.UTF8.GetString(read.Value!).Should().Be(Secret);
    }

    [Fact]
    public async Task ClientSecret_Absent_GetNull()
    {
        // Act
        var value = await Store().GetAsync(Acme, ClientSecret, CancellationToken.None);

        // Assert
        value.Should().BeNull();
    }

    [Fact]
    public async Task ClientSecret_SetAsync_NotSupported()
    {
        // Act
        var set = () => Store().SetAsync(Acme, ClientSecret, "x"u8.ToArray(), CancellationToken.None);
        var remove = () => Store().RemoveAsync(Acme, ClientSecret, CancellationToken.None);

        // Assert
        await set.Should().ThrowAsync<NotSupportedException>();
        await remove.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task Token_SetThenGet_RoundTrips_RemoveDeletes()
    {
        // Arrange
        var store = Store();

        // Act
        await store.SetAsync(Acme, Token, "{\"t\":1}"u8.ToArray(), CancellationToken.None);
        var value = await store.GetAsync(Acme, Token, CancellationToken.None);
        var removed = await store.RemoveAsync(Acme, Token, CancellationToken.None);
        var again = await store.RemoveAsync(Acme, Token, CancellationToken.None);

        // Assert
        Encoding.UTF8.GetString(value!.Value.Span).Should().Be("{\"t\":1}");
        removed.Should().BeTrue();
        again.Should().BeFalse();
        File.Exists(TokenFile).Should().BeFalse();
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "Unix file modes: Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task Token_SetThenGet_RoundTrips_File0600Dir0700_NoTemp_OnLinux()
    {
        // Arrange
        var store = LinuxStore();

        // Act
        await store.SetAsync(Acme, Token, "{\"t\":1}"u8.ToArray(), CancellationToken.None);
        var value = await store.GetAsync(Acme, Token, CancellationToken.None);

        // Assert
        Encoding.UTF8.GetString(value!.Value.Span).Should().Be("{\"t\":1}");
        File.GetUnixFileMode(TokenFile).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.GetUnixFileMode(LinkedInDirectory).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.EnumerateFiles(LinkedInDirectory).Should().Equal(TokenFile);
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "Unix file modes: Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task Token_BadMode_Refused_OnLinux()
    {
        // Arrange
        Write(TokenFile, "{}", "644");

        // Act
        var read = await LinuxStore().ReadAsync(Acme, Token, CancellationToken.None);

        // Assert
        read.Refusal.Should().Be($"linkedin: {TokenFile} must be mode 0600 (is 644)");
    }

    [Fact]
    public async Task Token_OtherTenant_Refused()
    {
        // Arrange
        Write(TokenFile, "{}", mode: null);
        var globex = TenantId.Parse("globex");

        // Act
        var get = () => Store().GetAsync(globex, Token, CancellationToken.None);
        var set = () => Store().SetAsync(globex, Token, "x"u8.ToArray(), CancellationToken.None);

        // Assert
        (await get.Should().ThrowAsync<CredentialRefusedException>()).Which.Message.Should().Be("linkedin: tenant globex is not this instance's");
        (await set.Should().ThrowAsync<CredentialRefusedException>()).Which.Message.Should().Be("linkedin: tenant globex is not this instance's");
    }

    [Fact(SkipUnless = nameof(IsWindows), Skip = "the platform refusal: Windows only")]
    public async Task Token_OwnershipCheckOffLinux_NotSupported()
    {
        // Arrange
        var store = new CredentialFileSecretStore(Acme, Env(false), KeyFile, checkOwnership: true, linkedInDirectory: LinkedInDirectory);

        // Act
        var read = await store.ReadAsync(Acme, Token, CancellationToken.None);
        var set = () => store.SetAsync(Acme, Token, "x"u8.ToArray(), CancellationToken.None);

        // Assert
        read.Refusal.Should().Be("linkedin: not supported on this platform");
        (await set.Should().ThrowAsync<CredentialRefusedException>()).Which.Message.Should().Be("linkedin: not supported on this platform");
    }

    [Fact]
    public async Task LinkedInNames_WithoutDirectory_NoCredential()
    {
        // Arrange
        var store = new CredentialFileSecretStore(Acme, Env(false), KeyFile, checkOwnership: false);

        // Act
        var read = await store.ReadAsync(Acme, Token, CancellationToken.None);

        // Assert
        read.Refusal.Should().Be("linkedin: no credential named linkedin/token");
    }

    [Fact]
    public async Task M365Names_Unchanged()
    {
        // Arrange
        Write(KeyFile, Pem, mode: null);
        var before = new CredentialFileSecretStore(Acme, Env(false), KeyFile, checkOwnership: false);
        var extended = Store();

        // Act
        var expected = await before.ReadAsync(Acme, AppKey, CancellationToken.None);
        var actual = await extended.ReadAsync(Acme, AppKey, CancellationToken.None);
        var absentBefore = await new CredentialFileSecretStore(Acme, Env(false), KeyFile + ".x", checkOwnership: false).ReadAsync(Acme, AppKey, CancellationToken.None);
        var absentAfter = await new CredentialFileSecretStore(Acme, Env(false), KeyFile + ".x", checkOwnership: false, linkedInDirectory: LinkedInDirectory).ReadAsync(Acme, AppKey, CancellationToken.None);

        // Assert
        actual.Should().BeEquivalentTo(expected);
        absentAfter.Should().BeEquivalentTo(absentBefore);
        var set = () => extended.SetAsync(Acme, AppKey, "x"u8.ToArray(), CancellationToken.None);
        await set.Should().ThrowAsync<NotSupportedException>();
    }

    public static bool IsWindows => OperatingSystem.IsWindows();

    private static void Write(string path, string content, string? mode)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        if (mode is not null && !OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, (UnixFileMode)Convert.ToInt32(mode, 8));
        }
    }

    private Dictionary<string, string?> Env(bool withCredentials) =>
        new() { ["CREDENTIALS_DIRECTORY"] = withCredentials ? CredentialsDirectory : null };

    private CredentialFileSecretStore Store(bool withCredentials = false) =>
        new(Acme, Env(withCredentials), KeyFile, checkOwnership: false, linkedInDirectory: LinkedInDirectory);

    [SupportedOSPlatform("linux")]
    private CredentialFileSecretStore LinuxStore(bool withCredentials = false) =>
        new(Acme, Env(withCredentials), KeyFile, checkOwnership: true, linkedInDirectory: LinkedInDirectory);
}
