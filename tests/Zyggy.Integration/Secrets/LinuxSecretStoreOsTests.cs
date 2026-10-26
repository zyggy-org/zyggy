using System.Runtime.Versioning;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Secrets;

/// <summary>
/// Spec 09 AC-10 on a real Linux file system: the production <c>LinuxSecretStore</c> (real owner lookup) as the systemd unit will
/// see it — a 0700 credentials directory with a 0400 id is read, the file layer writes 0600 files in 0700 directories, a 0644
/// credentials file is refused naming the mode. Skipped visibly on Windows; every fact cleans its tree in <c>finally</c>.
/// </summary>
[Trait("Category", "OsStore")]
public sealed class LinuxSecretStoreOsTests : IDisposable
{
    private static readonly TenantId Acme = TenantId.Parse("acme");
    private static readonly SecretName Hmac1 = SecretName.Parse("hmac/1");
    private static readonly SecretName GitHubPat = SecretName.Parse("github-pat");

    private readonly ScratchDirectory _scratch = new();

    public static bool IsLinux => OperatingSystem.IsLinux();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Key => [.. Enumerable.Range(1, 32).Select(i => (byte)i)];

    private string Credentials => _scratch.File("credentials");

    private string Root => _scratch.File("secrets");

    public void Dispose()
    {
        string path = _scratch.Path;
        _scratch.Dispose();
        Directory.Exists(path).Should().BeFalse("every fact deletes its tree in finally");
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "Unix file modes and owners: Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task Get_CredentialsDirectory0700With0400File_ReadsRawBytes()
    {
        // Arrange
        string id = Arrange0700DirectoryWithId(UnixFileMode.UserRead);
        try
        {
            // Act
            ReadOnlyMemory<byte>? value = await Store().GetAsync(Acme, Hmac1, Ct);

            // Assert
            value!.Value.ToArray().Should().Equal(Key);
        }
        finally
        {
            Cleanup(id);
        }
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "Unix file modes and owners: Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task Set_GitHubPat_FileLayer0600InDirectories0700()
    {
        try
        {
            // Act
            await Store().SetAsync(Acme, GitHubPat, Key, Ct);

            // Assert
            File.GetUnixFileMode(Path.Combine(Root, "acme", "github-pat")).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.GetUnixFileMode(Path.Combine(Root, "acme")).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        finally
        {
            Cleanup(null);
        }
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "Unix file modes and owners: Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task Get_0644CredentialsFile_UnavailableNamesMode()
    {
        // Arrange
        string id = Arrange0700DirectoryWithId(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        try
        {
            // Act
            Func<Task> act = () => Store().GetAsync(Acme, Hmac1, Ct);

            // Assert
            string message = (await act.Should().ThrowAsync<SecretStoreUnavailableException>()).Which.Message;
            message.Should().Contain(id).And.Contain("(is 644)");
            SecretSweep.AssertAbsent([message]);
        }
        finally
        {
            Cleanup(id);
        }
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "Unix file modes and owners: Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task List_UnionAcrossBothLayers()
    {
        // Arrange
        string id = Arrange0700DirectoryWithId(UnixFileMode.UserRead);
        try
        {
            LinuxSecretStore store = Store();
            await store.SetAsync(Acme, GitHubPat, Key, Ct);

            // Act
            IReadOnlyList<SecretName> names = await store.ListAsync(Acme, Ct);

            // Assert
            names.Select(n => n.Value).Should().Equal("github-pat", "hmac/1");
        }
        finally
        {
            Cleanup(id);
        }
    }

    [SupportedOSPlatform("linux")]
    private string Arrange0700DirectoryWithId(UnixFileMode idMode)
    {
        Directory.CreateDirectory(Credentials, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string id = Path.Combine(Credentials, LinuxSecretStore.CredentialId(Acme, Hmac1));
        File.WriteAllBytes(id, Key);
        File.SetUnixFileMode(id, idMode);
        return id;
    }

    private LinuxSecretStore Store()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal) { ["CREDENTIALS_DIRECTORY"] = Credentials };
        var files = new FileSecretStore(Options.Create(new FileSecretStoreOptions { RootDirectory = Root }));
        return new LinuxSecretStore(environment, files, NullLogger<LinuxSecretStore>.Instance);
    }

    // Modes reset so the delete succeeds; then the whole tree goes.
    [SupportedOSPlatform("linux")]
    private void Cleanup(string? id)
    {
        if (id is not null && File.Exists(id))
        {
            File.SetUnixFileMode(id, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        foreach (string directory in new[] { Credentials, Root }.Where(Directory.Exists))
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(directory, recursive: true);
        }
    }
}
