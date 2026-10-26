using Microsoft.Extensions.Logging;

using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Secrets;

/// <summary>
/// Central's store (spec 09 AC-10, founding spec §8 as amended by A9-2): the systemd credentials directory first —
/// <c>$CREDENTIALS_DIRECTORY/zyggy.&lt;tenant&gt;.&lt;name with / → .&gt;</c>, raw bytes as stored, read-only, mode 0400/0440/0600 and owned
/// by this user or root — then the 0600 file layer (<see cref="FileSecretStore"/>) for reads and every write. A name present
/// only in the directory cannot be removed. Never caches, never logs a value, never puts a byte in a message.
/// </summary>
internal sealed partial class LinuxSecretStore : ISecretStore, IDescribedSecretStore
{
    private const string CredentialsDirectoryVariable = "CREDENTIALS_DIRECTORY";
    private const string IdPrefix = "zyggy.";

    private readonly string? _credentialsDirectory;
    private readonly FileSecretStore _files;
    private readonly ILogger<LinuxSecretStore> _log;
    private readonly bool _checkOwnership;
    private readonly Func<string, uint?> _ownerOf;
    private readonly Func<uint> _effectiveUser;
    private bool _missingDirectoryLogged;

    public LinuxSecretStore(IReadOnlyDictionary<string, string?> environment, FileSecretStore files, ILogger<LinuxSecretStore> log)
        : this(environment, files, log, checkOwnership: true)
    {
    }

    // Tests on any OS: the mode and owner checks substituted, as CredentialFileSecretStore is tested.
    internal LinuxSecretStore(
        IReadOnlyDictionary<string, string?> environment,
        FileSecretStore files,
        ILogger<LinuxSecretStore> log,
        bool checkOwnership,
        Func<string, uint?>? ownerOf = null,
        Func<uint>? effectiveUser = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(log);
        _credentialsDirectory = environment.TryGetValue(CredentialsDirectoryVariable, out var directory) && !string.IsNullOrEmpty(directory) ? directory : null;
        _files = files;
        _log = log;
        _checkOwnership = checkOwnership;
        _ownerOf = ownerOf ?? Processes.UnixNative.Owner;
        _effectiveUser = effectiveUser ?? Processes.UnixNative.EffectiveUserId;
    }

    /// <summary>The credentials directory when the variable names an existing directory, else the file layer.</summary>
    public string StoreName => DirectoryOrNull() is not null ? SecretStoreNames.CredentialsDirectory : SecretStoreNames.File;

    /// <summary>The systemd credential id of a secret: <c>zyggy.&lt;tenant&gt;.&lt;name with / → .&gt;</c> (spec 09 Contracts, ex-Q6) — what the unit's <c>LoadCredential=</c> names.</summary>
    internal static string CredentialId(TenantId tenant, SecretName name)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(name);
        return IdPrefix + tenant.Value + "." + name.Value.Replace('/', '.');
    }

    public async Task<ReadOnlyMemory<byte>?> GetAsync(TenantId tenant, SecretName name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(name);
        if (DirectoryOrNull() is { } directory)
        {
            string path = Path.Join(directory, CredentialId(tenant, name));
            if (File.Exists(path))
            {
                return await ReadCredentialAsync(path, cancellationToken).ConfigureAwait(false);
            }
        }

        return await _files.GetAsync(tenant, name, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes the file layer; the credentials directory is never written.</summary>
    public Task SetAsync(TenantId tenant, SecretName name, ReadOnlyMemory<byte> value, CancellationToken cancellationToken) =>
        _files.SetAsync(tenant, name, value, cancellationToken);

    /// <summary>Removes from the file layer; a name present only in the credentials directory answers <see langword="false"/>.</summary>
    public Task<bool> RemoveAsync(TenantId tenant, SecretName name, CancellationToken cancellationToken) =>
        _files.RemoveAsync(tenant, name, cancellationToken);

    public async Task<IReadOnlyList<SecretName>> ListAsync(TenantId tenant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        IReadOnlyList<SecretName> fromFiles = await _files.ListAsync(tenant, cancellationToken).ConfigureAwait(false);
        return DirectoryNames(tenant)
            .Concat(fromFiles)
            .DistinctBy(n => n.Value, StringComparer.Ordinal)
            .OrderBy(n => n.Value, StringComparer.Ordinal)
            .ToList();
    }

    public ValueTask<string> SourceOfAsync(TenantId tenant, SecretName name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(name);
        cancellationToken.ThrowIfCancellationRequested();
        bool inDirectory = DirectoryOrNull() is { } directory && File.Exists(Path.Join(directory, CredentialId(tenant, name)));
        return ValueTask.FromResult(inDirectory ? SecretStoreNames.CredentialsDirectory : SecretStoreNames.File);
    }

    // The names this tenant's ids map back to; an id that is not a valid name (or another tenant's) is skipped.
    private IEnumerable<SecretName> DirectoryNames(TenantId tenant)
    {
        if (DirectoryOrNull() is not { } directory)
        {
            yield break;
        }

        string prefix = IdPrefix + tenant.Value + ".";
        foreach (string file in Directory.EnumerateFiles(directory))
        {
            string id = Path.GetFileName(file);
            if (id.StartsWith(prefix, StringComparison.Ordinal) && SecretName.TryParse(id[prefix.Length..].Replace('.', '/'), out SecretName? name))
            {
                yield return name;
            }
        }
    }

    // The variable set but naming no directory: the unit has no LoadCredential= lines (or this is not the unit) — said once per instance.
    private string? DirectoryOrNull()
    {
        if (_credentialsDirectory is null)
        {
            return null;
        }

        if (Directory.Exists(_credentialsDirectory))
        {
            return _credentialsDirectory;
        }

        if (!_missingDirectoryLogged)
        {
            _missingDirectoryLogged = true;
            LogDirectoryMissing(_credentialsDirectory);
        }

        return null;
    }

    private async Task<ReadOnlyMemory<byte>> ReadCredentialAsync(string path, CancellationToken cancellationToken)
    {
        if (_checkOwnership && OperatingSystem.IsLinux())
        {
            int mode = (int)File.GetUnixFileMode(path) & 0x1FF;
            if (mode is not (0x100 or 0x120 or 0x180))
            {
                throw new SecretStoreUnavailableException(SecretStoreNames.CredentialsDirectory, $"{path} must be mode 0400, 0440 or 0600 (is {Convert.ToString(mode, 8)})");
            }

            uint? owner = _ownerOf(path);
            if (owner is null || (owner != _effectiveUser() && owner != 0))
            {
                throw new SecretStoreUnavailableException(SecretStoreNames.CredentialsDirectory, $"{path} must be owned by {Environment.UserName} or root");
            }
        }

        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        if (bytes.Length == 0)
        {
            throw new SecretStoreUnavailableException(SecretStoreNames.CredentialsDirectory, $"{path} is empty");
        }

        return bytes;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "credentials directory {Path} is set but missing; using the file layer")]
    private partial void LogDirectoryMissing(string path);
}
