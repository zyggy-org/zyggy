
using System.Text;
using System.Text.RegularExpressions;

using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Secrets;

/// <summary>One read of a credential: its bytes and source, or the shell's refusal text.</summary>
internal sealed record CredentialRead(byte[]? Value, CredentialSource Source, string? Path, string? Refusal);

/// <summary>Reads one credential with its source or refusal (the store's read path, substituted in tests).</summary>
internal interface ICredentialReader
{
    Task<CredentialRead> ReadAsync(TenantId tenant, SecretName name, CancellationToken cancellationToken);
}

/// <summary>
/// The read-only <see cref="ISecretStore"/> over Central's credential files (spec 33 AC-4), for one tenant — the configured
/// principal's. <c>m365-app-key</c> is <c>$CREDENTIALS_DIRECTORY/m365-app-key</c> (0600, 0400 or 0440; owned by this user or by root) else the key file (0600);
/// <c>m365-app-key/new</c> is <c>&lt;key file&gt;.new</c>. The checks and texts are <c>zy_m365_read_key</c>'s: a regular file, the
/// mode, owned by this user, not empty, a PEM private-key header. Never caches, never logs, never puts a byte in a message.
/// Writing is <c>cert-init</c>'s alone.
/// </summary>
internal sealed partial class CredentialFileSecretStore : ISecretStore, ICredentialReader
{
    public const string AppKeyName = "m365-app-key";
    public const string NewAppKeyName = "m365-app-key/new";

    private readonly TenantId _tenant;
    private readonly string? _credentialsDirectory;
    private readonly string _keyFile;
    private readonly bool _checkOwnership;
    private readonly Func<string, uint?> _ownerOf;
    private readonly Func<uint> _effectiveUser;

    public CredentialFileSecretStore(TenantId tenant, IReadOnlyDictionary<string, string?> environment, string keyFile)
        : this(tenant, environment, keyFile, checkOwnership: true)
    {
    }

    // Tests on Windows: no Unix mode or owner to check; on Linux the owner lookup and the effective user may be given.
    internal CredentialFileSecretStore(
        TenantId tenant,
        IReadOnlyDictionary<string, string?> environment,
        string keyFile,
        bool checkOwnership,
        Func<string, uint?>? ownerOf = null,
        Func<uint>? effectiveUser = null)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrEmpty(keyFile);
        _tenant = tenant;
        _credentialsDirectory = environment.TryGetValue("CREDENTIALS_DIRECTORY", out var directory) && !string.IsNullOrEmpty(directory) ? directory : null;
        _keyFile = keyFile;
        _checkOwnership = checkOwnership;
        _ownerOf = ownerOf ?? Processes.UnixNative.Owner;
        _effectiveUser = effectiveUser ?? Processes.UnixNative.EffectiveUserId;
    }

    public async Task<ReadOnlyMemory<byte>?> GetAsync(TenantId tenant, SecretName name, CancellationToken cancellationToken)
    {
        var read = await ReadAsync(tenant, name, cancellationToken).ConfigureAwait(false);
        if (read.Value is { } value)
        {
            return value;
        }

        return read.Refusal == NotFound ? null : throw new CredentialRefusedException(read.Refusal!);
    }

    public Task SetAsync(TenantId tenant, SecretName name, ReadOnlyMemory<byte> value, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The credential files are read-only; cert-init writes the key pair.");

    public Task<bool> RemoveAsync(TenantId tenant, SecretName name, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The credential files are read-only.");

    private const string NotFound = "key: not found in credentials directory or file";

    /// <summary>Reads a credential with the shell's location order and checks.</summary>
    public Task<CredentialRead> ReadAsync(TenantId tenant, SecretName name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(name);
        cancellationToken.ThrowIfCancellationRequested();
        if (tenant != _tenant)
        {
            return Task.FromResult(Refuse($"key: tenant {tenant.Value} is not this instance's"));
        }

        if (_checkOwnership && !OperatingSystem.IsLinux())
        {
            return Task.FromResult(Refuse("key: not supported on this platform"));
        }

        string path;
        CredentialSource source;
        switch (name.Value)
        {
            case NewAppKeyName:
                (path, source) = (_keyFile + ".new", CredentialSource.File);
                break;
            case AppKeyName when _credentialsDirectory is not null && Exists(Path.Join(_credentialsDirectory, AppKeyName)):
                (path, source) = (Path.Join(_credentialsDirectory, AppKeyName), CredentialSource.CredentialsDirectory);
                break;
            case AppKeyName when Exists(_keyFile):
                (path, source) = (_keyFile, CredentialSource.File);
                break;
            case AppKeyName:
                return Task.FromResult(Refuse(NotFound));
            default:
                return Task.FromResult(Refuse($"key: no credential named {name.Value}"));
        }

        return Task.FromResult(Check(path, source));
    }

    private CredentialRead Check(string path, CredentialSource source)
    {
        // [ -f ] follows a link, as the shell's test does.
        if (!File.Exists(path))
        {
            return Refuse($"key: {path} is not a regular file");
        }

        if (_checkOwnership && OperatingSystem.IsLinux())
        {
            var mode = (int)File.GetUnixFileMode(path) & 0x1FF;
            // systemd's LoadCredential copy is read-only: 0400, or 0440 (group = the unit's group) on newer systemd.
            var accepted = mode == 0x180 || (source == CredentialSource.CredentialsDirectory && mode is 0x100 or 0x120);
            if (!accepted)
            {
                return Refuse($"key: {path} must be mode 0600 (is {Convert.ToString(mode, 8)})");
            }

            // The LoadCredential copy is root's (group = the unit's group, hence 0440); the key file is the service user's.
            var owned = _ownerOf(path) is { } uid && (uid == _effectiveUser() || (uid == 0 && source == CredentialSource.CredentialsDirectory));
            if (!owned)
            {
                return Refuse($"key: {path} must be owned by {Environment.UserName}");
            }
        }

        var bytes = File.ReadAllBytes(path);
        if (bytes.Length == 0)
        {
            return Refuse($"key: {path} is empty");
        }

        // grep -qE -m 1 '^-----BEGIN (RSA |EC )?PRIVATE KEY-----$': any line, only the header line is decoded.
        if (!HasPemHeader(bytes))
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
            return Refuse($"key: {path} has no PEM private-key header");
        }

        return new CredentialRead(bytes, source, path, null);
    }

    private static bool HasPemHeader(byte[] bytes)
    {
        var start = 0;
        while (start < bytes.Length)
        {
            var end = Array.IndexOf(bytes, (byte)'\n', start);
            var line = bytes.AsSpan(start, (end < 0 ? bytes.Length : end) - start);
            if (line.Length < 64 && line.StartsWith("-----BEGIN "u8) && PemHeader().IsMatch(Encoding.ASCII.GetString(line)))
            {
                return true;
            }

            start = end < 0 ? bytes.Length : end + 1;
        }

        return false;
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null;

    private static CredentialRead Refuse(string message) => new(null, CredentialSource.None, null, message);

    [GeneratedRegex(@"\A-----BEGIN (RSA |EC )?PRIVATE KEY-----\z", RegexOptions.CultureInvariant)]
    private static partial Regex PemHeader();
}
