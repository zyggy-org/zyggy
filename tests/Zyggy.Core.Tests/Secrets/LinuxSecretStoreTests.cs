using System.Runtime.Versioning;
using System.Text;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Secrets;

/// <summary>
/// Spec 09 AC-10, the Fake half: Central's store — the systemd credentials directory first (raw bytes, read-only), then the
/// 0600 file layer (read/write) — over scratch directories, the mode and owner checks substituted as <c>CredentialFileSecretStore</c> is tested.
/// </summary>
public sealed class LinuxSecretStoreTests : IDisposable
{
    private static readonly TenantId Acme = TenantId.Parse("acme");
    private static readonly SecretName Hmac1 = SecretName.Parse("hmac/1");
    private static readonly SecretName Hmac2 = SecretName.Parse("hmac/2");
    private static readonly SecretName GitHubPat = SecretName.Parse("github-pat");

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "zyggy-ut", Guid.NewGuid().ToString("N"));
    private readonly RecordingLogger<LinuxSecretStore> _log = new();

    public LinuxSecretStoreTests()
    {
        Directory.CreateDirectory(CredentialsDirectory);
        Directory.CreateDirectory(Root);
    }

    public static bool IsLinux => OperatingSystem.IsLinux();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Key => [.. Enumerable.Range(1, 32).Select(i => (byte)i)];

    private string CredentialsDirectory => Path.Combine(_scratch, "credentials");

    private string Root => Path.Combine(_scratch, "secrets");

    public void Dispose() => Directory.Delete(_scratch, recursive: true);

    [Fact]
    public async Task GetAsync_CredentialsDirectoryHasId_ReturnsRawBytesAsStored()
    {
        // Arrange: 32 bytes and a trailing newline written deliberately — the store never trims
        byte[] stored = [.. Key, (byte)'\n'];
        await File.WriteAllBytesAsync(Path.Combine(CredentialsDirectory, "zyggy.acme.hmac.1"), stored, Ct);

        // Act
        ReadOnlyMemory<byte>? value = await Store().GetAsync(Acme, Hmac1, Ct);

        // Assert
        value!.Value.ToArray().Should().Equal(stored);
    }

    [Fact]
    public async Task GetAsync_OnlyFileLayer_ReturnsDecodedHex()
    {
        // Arrange
        LinuxSecretStore store = Store();
        await store.SetAsync(Acme, Hmac1, Key, Ct);

        // Act
        ReadOnlyMemory<byte>? value = await store.GetAsync(Acme, Hmac1, Ct);

        // Assert
        value!.Value.ToArray().Should().Equal(Key);
    }

    [Fact]
    public async Task GetAsync_BothPresentDifferentBytes_DirectoryWins()
    {
        // Arrange
        LinuxSecretStore store = Store();
        await store.SetAsync(Acme, Hmac1, Key, Ct);
        byte[] fromDirectory = Encoding.ASCII.GetBytes("from-the-credentials-directory!!");
        await File.WriteAllBytesAsync(Path.Combine(CredentialsDirectory, "zyggy.acme.hmac.1"), fromDirectory, Ct);

        // Act
        ReadOnlyMemory<byte>? value = await store.GetAsync(Acme, Hmac1, Ct);

        // Assert
        value!.Value.ToArray().Should().Equal(fromDirectory);
    }

    [Fact]
    public async Task GetAsync_NeitherPresent_Null()
    {
        // Act
        ReadOnlyMemory<byte>? value = await Store().GetAsync(Acme, Hmac1, Ct);

        // Assert
        value.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_VariableSetDirectoryMissing_FallsThroughAndLogsDebugOnce()
    {
        // Arrange: the variable names a directory that does not exist
        LinuxSecretStore store = Store(credentialsDirectory: Path.Combine(_scratch, "gone"));
        await store.SetAsync(Acme, Hmac1, Key, Ct);

        // Act
        ReadOnlyMemory<byte>? first = await store.GetAsync(Acme, Hmac1, Ct);
        ReadOnlyMemory<byte>? second = await store.GetAsync(Acme, Hmac1, Ct);

        // Assert
        first!.Value.ToArray().Should().Equal(Key);
        second!.Value.ToArray().Should().Equal(Key);
        _log.AtLevel(LogLevel.Debug).Should().ContainSingle().Which.Message.Should().Contain("is set but missing; using the file layer");
    }

    [Fact]
    public async Task GetAsync_VariableUnset_FileLayerOnly()
    {
        // Arrange: a directory id that would win if the variable were set
        await File.WriteAllBytesAsync(Path.Combine(CredentialsDirectory, "zyggy.acme.hmac.1"), Key, Ct);
        LinuxSecretStore store = Store(credentialsDirectory: null);

        // Act
        ReadOnlyMemory<byte>? value = await store.GetAsync(Acme, Hmac1, Ct);

        // Assert
        value.Should().BeNull();
        _log.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAsync_OtherTenantIdInDirectory_NotRead()
    {
        // Arrange
        await File.WriteAllBytesAsync(Path.Combine(CredentialsDirectory, "zyggy.other-org.hmac.1"), Key, Ct);

        // Act
        ReadOnlyMemory<byte>? value = await Store().GetAsync(Acme, Hmac1, Ct);

        // Assert
        value.Should().BeNull();
    }

    [Fact]
    public async Task SetAsync_WritesFileLayerNeverDirectory()
    {
        // Act
        await Store().SetAsync(Acme, GitHubPat, Encoding.UTF8.GetBytes(TestKeys.TestPat), Ct);

        // Assert
        Directory.EnumerateFileSystemEntries(CredentialsDirectory).Should().BeEmpty();
        (await File.ReadAllTextAsync(Path.Combine(Root, "acme", "github-pat"), Ct)).Trim().Should().Be(Convert.ToHexStringLower(Encoding.UTF8.GetBytes(TestKeys.TestPat)));
    }

    [Fact]
    public async Task RemoveAsync_NamePresentOnlyInDirectory_FalseAndDirectoryUntouched()
    {
        // Arrange
        string id = Path.Combine(CredentialsDirectory, "zyggy.acme.hmac.1");
        await File.WriteAllBytesAsync(id, Key, Ct);

        // Act
        bool removed = await Store().RemoveAsync(Acme, Hmac1, Ct);

        // Assert
        removed.Should().BeFalse();
        File.Exists(id).Should().BeTrue();
    }

    [Fact]
    public async Task RemoveAsync_FileLayer_True()
    {
        // Arrange
        LinuxSecretStore store = Store();
        await store.SetAsync(Acme, Hmac1, Key, Ct);

        // Act
        bool removed = await store.RemoveAsync(Acme, Hmac1, Ct);

        // Assert
        removed.Should().BeTrue();
        (await store.GetAsync(Acme, Hmac1, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task ListAsync_Union_EachNameOnceOrdinal()
    {
        // Arrange: hmac/1 in both layers, hmac/2 in the directory only, github-pat in the file layer only, a foreign tenant's id
        LinuxSecretStore store = Store();
        await store.SetAsync(Acme, Hmac1, Key, Ct);
        await store.SetAsync(Acme, GitHubPat, Key, Ct);
        await File.WriteAllBytesAsync(Path.Combine(CredentialsDirectory, "zyggy.acme.hmac.1"), Key, Ct);
        await File.WriteAllBytesAsync(Path.Combine(CredentialsDirectory, "zyggy.acme.hmac.2"), Key, Ct);
        await File.WriteAllBytesAsync(Path.Combine(CredentialsDirectory, "zyggy.other-org.hmac.3"), Key, Ct);

        // Act
        IReadOnlyList<SecretName> names = await store.ListAsync(Acme, Ct);

        // Assert
        names.Select(n => n.Value).Should().Equal("github-pat", "hmac/1", "hmac/2");
    }

    [Fact]
    public async Task SourceOfAsync_PerEntry()
    {
        // Arrange
        LinuxSecretStore store = Store();
        await store.SetAsync(Acme, GitHubPat, Key, Ct);
        await File.WriteAllBytesAsync(Path.Combine(CredentialsDirectory, "zyggy.acme.hmac.1"), Key, Ct);

        // Act
        string hmac = await store.SourceOfAsync(Acme, Hmac1, Ct);
        string pat = await store.SourceOfAsync(Acme, GitHubPat, Ct);

        // Assert
        hmac.Should().Be("credentials-directory");
        pat.Should().Be("file");
        store.StoreName.Should().Be("credentials-directory");
        Store(credentialsDirectory: null).StoreName.Should().Be("file");
    }

    [Theory]
    [InlineData("hmac/1", "zyggy.acme.hmac.1")]
    [InlineData("linkedin/token", "zyggy.acme.linkedin.token")]
    [InlineData("github-pat", "zyggy.acme.github-pat")]
    public void CredentialId_SlashesBecomeDots(string name, string expected)
    {
        // Act
        string id = LinuxSecretStore.CredentialId(Acme, SecretName.Parse(name));

        // Assert
        id.Should().Be(expected);
    }

    [Fact]
    public void CredentialId_DistinctNames_DistinctIds()
    {
        // Arrange: names a tenant could plausibly hold at once
        string[] names = ["hmac/1", "hmac/10", "hmac/2", "github-pat", "linkedin/token", "linkedin/client-secret", "m365-app-key", "m365-app-key/new"];

        // Act
        var ids = names.Select(n => LinuxSecretStore.CredentialId(Acme, SecretName.Parse(n))).ToList();

        // Assert
        ids.Should().OnlyHaveUniqueItems();
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "Unix file modes: Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task GetAsync_OnLinux_Mode0644CredentialsFile_UnavailableNamingPathAndMode()
    {
        // Arrange
        string id = Path.Combine(CredentialsDirectory, "zyggy.acme.hmac.1");
        await File.WriteAllBytesAsync(id, Key, Ct);
        File.SetUnixFileMode(id, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        // Act
        Func<Task> act = () => Store(checkOwnership: true).GetAsync(Acme, Hmac1, Ct);

        // Assert
        var exception = (await act.Should().ThrowAsync<SecretStoreUnavailableException>()).Which;
        exception.StoreName.Should().Be("credentials-directory");
        exception.Message.Should().Contain(id).And.Contain("(is 644)");
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "Unix owner: Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task GetAsync_OnLinux_OwnerNeitherMeNorRoot_Unavailable()
    {
        // Arrange
        string id = Path.Combine(CredentialsDirectory, "zyggy.acme.hmac.1");
        await File.WriteAllBytesAsync(id, Key, Ct);
        File.SetUnixFileMode(id, UnixFileMode.UserRead);

        // Act
        Func<Task> act = () => Store(checkOwnership: true, ownerOf: _ => 12345, effectiveUser: () => 1000).GetAsync(Acme, Hmac1, Ct);

        // Assert
        (await act.Should().ThrowAsync<SecretStoreUnavailableException>()).Which.Message.Should().Contain("must be owned by");
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "Unix owner: Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task GetAsync_OnLinux_RootOwned0440_Accepted()
    {
        // Arrange: systemd's copy — root's, 0440
        string id = Path.Combine(CredentialsDirectory, "zyggy.acme.hmac.1");
        await File.WriteAllBytesAsync(id, Key, Ct);
        File.SetUnixFileMode(id, UnixFileMode.UserRead | UnixFileMode.GroupRead);

        // Act
        ReadOnlyMemory<byte>? value = await Store(checkOwnership: true, ownerOf: _ => 0, effectiveUser: () => 1000).GetAsync(Acme, Hmac1, Ct);

        // Assert
        value!.Value.ToArray().Should().Equal(Key);
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "Unix file modes: Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task GetAsync_OnLinux_FileLayerNot0600_StillRead()
    {
        // Arrange: the file layer never checked modes on read (04) — unchanged
        LinuxSecretStore store = Store(checkOwnership: true);
        await store.SetAsync(Acme, Hmac1, Key, Ct);
        File.SetUnixFileMode(Path.Combine(Root, "acme", "hmac", "1"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

        // Act
        ReadOnlyMemory<byte>? value = await store.GetAsync(Acme, Hmac1, Ct);

        // Assert
        value!.Value.ToArray().Should().Equal(Key);
    }

    [Fact]
    public async Task Messages_NeverContainBytes()
    {
        // Arrange: an empty credentials file (unavailable, A4) and a missing directory (one Debug line)
        await File.WriteAllBytesAsync(Path.Combine(CredentialsDirectory, "zyggy.acme.hmac.1"), [], Ct);
        var texts = new List<string?>();

        // Act
        try
        {
            await Store().GetAsync(Acme, Hmac1, Ct);
        }
        catch (SecretStoreUnavailableException ex)
        {
            texts.Add(ex.Message);
        }

        await Store(credentialsDirectory: Path.Combine(_scratch, "gone")).GetAsync(Acme, Hmac1, Ct);
        texts.AddRange(_log.Messages);

        // Assert
        texts.Should().HaveCount(2);
        texts[0].Should().Contain("is empty");
        SecretSweep.AssertAbsent(texts);
    }

    private LinuxSecretStore Store(string? credentialsDirectory = "", bool checkOwnership = false, Func<string, uint?>? ownerOf = null, Func<uint>? effectiveUser = null)
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["CREDENTIALS_DIRECTORY"] = credentialsDirectory == string.Empty ? CredentialsDirectory : credentialsDirectory,
        };
        var files = new FileSecretStore(Options.Create(new FileSecretStoreOptions { RootDirectory = Root }));
        return new LinuxSecretStore(environment, files, _log, checkOwnership, ownerOf, effectiveUser);
    }
}
