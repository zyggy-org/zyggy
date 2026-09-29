using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

using Microsoft.Extensions.Options;

using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Secrets;

/// <summary>
/// Stores each secret as one lowercase hex line in <c>&lt;root&gt;/&lt;tenant&gt;/&lt;name&gt;</c>, readable by the owner only:
/// mode 0600 (directories 0700) on Linux, a protected DACL granting only the current user on Windows (founding spec §8, P0
/// file store). No cache: a rotated key is visible on the next lookup. Never logs, and never puts key bytes in a message.
/// </summary>
internal sealed class FileSecretStore : ISecretStore
{
    private const UnixFileMode OwnerDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode OwnerFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _root;

    public FileSecretStore(IOptions<FileSecretStoreOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        string? root = options.Value.RootDirectory
            ?? DefaultRoot();
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException(
                "FileSecretStoreOptions.RootDirectory must be set: no user profile directory is available to hold the secret store.");
        }

        _root = Path.GetFullPath(root);
    }

    public async Task<ReadOnlyMemory<byte>?> GetAsync(TenantId tenant, SecretName name, CancellationToken cancellationToken)
    {
        string path = PathOf(tenant, name);
        if (!File.Exists(path))
        {
            return null;
        }

        string text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        try
        {
            return Convert.FromHexString(text.Trim());
        }
        catch (FormatException)
        {
            throw new InvalidDataException($"Secret file '{path}' does not contain an even number of hexadecimal characters.");
        }
    }

    public async Task SetAsync(TenantId tenant, SecretName name, ReadOnlyMemory<byte> value, CancellationToken cancellationToken)
    {
        string path = PathOf(tenant, name);
        if (value.IsEmpty)
        {
            throw new ArgumentException("A secret value must not be empty.", nameof(value));
        }

        byte[] content = Utf8NoBom.GetBytes(Convert.ToHexStringLower(value.Span) + "\n");
        CreateOwnerOnlyDirectories(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            FileStream stream = OperatingSystem.IsWindows() ? CreateOwnerOnlyFileWindows(temporary) : CreateOwnerOnlyFileUnix(temporary);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            // The final path never exists with permissive permissions: the owner-only file replaces it atomically.
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            File.Delete(temporary);
            throw;
        }
    }

    public Task<bool> RemoveAsync(TenantId tenant, SecretName name, CancellationToken cancellationToken)
    {
        string path = PathOf(tenant, name);
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path))
        {
            return Task.FromResult(false);
        }

        File.Delete(path);
        return Task.FromResult(true);
    }

    private static string DefaultRoot()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return profile.Length == 0 ? string.Empty : Path.Combine(profile, "zyggy", "secrets");
    }

    [SupportedOSPlatform("windows")]
    private static FileStream CreateOwnerOnlyFileWindows(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        SecurityIdentifier user = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current Windows identity has no user SID; the secret file cannot be restricted to it.");
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Write, FileShare.None, 4096, FileOptions.None, security);
    }

    [UnsupportedOSPlatform("windows")]
    private static FileStream CreateOwnerOnlyFileUnix(string path) => new(path, new FileStreamOptions
    {
        Mode = FileMode.CreateNew,
        Access = FileAccess.Write,
        Share = FileShare.None,
        UnixCreateMode = OwnerFile,
    });

    // Every segment <tenant>/<name...> is validated, so no path here can leave the root.
    private string PathOf(TenantId tenant, SecretName name)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(name);
        return Path.Combine([_root, tenant.Value, .. name.Value.Split('/')]);
    }

    // Level by level so every directory Zyggy creates below the root is 0700 on Unix.
    private void CreateOwnerOnlyDirectories(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
            return;
        }

        Directory.CreateDirectory(_root, OwnerDirectory);
        string current = _root;
        foreach (string segment in Path.GetRelativePath(_root, directory).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            Directory.CreateDirectory(current, OwnerDirectory);
        }
    }
}
