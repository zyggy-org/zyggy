
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
/// Writing the m365 key is <c>cert-init</c>'s alone.
/// <para>
/// LinkedIn (spec 36 AC-19), when a LinkedIn directory is given: <c>linkedin/client-secret</c> (read-only) is
/// <c>$CREDENTIALS_DIRECTORY/linkedin-client-secret</c> (0600, 0400 or 0440; this user or root) else <c>&lt;dir&gt;/client-secret</c>
/// (0600, this user): not empty, one line, no PEM check; the value is returned without its trailing newline.
/// <c>linkedin/token</c> is <c>&lt;dir&gt;/token.json</c> (0600, this user, not empty), written as a 0600 temporary file renamed in the
/// 0700 directory and removed by deleting it. Their refusals start with <c>linkedin:</c>.
/// </para>
/// </summary>
internal sealed partial class CredentialFileSecretStore : ISecretStore, ICredentialReader
{
    public const string AppKeyName = "m365-app-key";
    public const string NewAppKeyName = "m365-app-key/new";
    public const string LinkedInClientSecretName = "linkedin/client-secret";
    public const string LinkedInTokenName = "linkedin/token";

    private const string LinkedInCredentialsFile = "linkedin-client-secret";
    private const string LinkedInClientSecretFile = "client-secret";
    private const string LinkedInTokenFile = "token.json";

    private readonly TenantId _tenant;
    private readonly string? _credentialsDirectory;
    private readonly string _keyFile;
    private readonly string? _linkedInDirectory;
    private readonly bool _checkOwnership;
    private readonly Func<string, uint?> _ownerOf;
    private readonly Func<uint> _effectiveUser;

    public CredentialFileSecretStore(TenantId tenant, IReadOnlyDictionary<string, string?> environment, string keyFile, string? linkedInDirectory = null)
        : this(tenant, environment, keyFile, checkOwnership: true, linkedInDirectory: linkedInDirectory)
    {
    }

    // Tests on Windows: no Unix mode or owner to check; on Linux the owner lookup and the effective user may be given.
    internal CredentialFileSecretStore(
        TenantId tenant,
        IReadOnlyDictionary<string, string?> environment,
        string keyFile,
        bool checkOwnership,
        Func<string, uint?>? ownerOf = null,
        Func<uint>? effectiveUser = null,
        string? linkedInDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrEmpty(keyFile);
        _tenant = tenant;
        _credentialsDirectory = environment.TryGetValue("CREDENTIALS_DIRECTORY", out var directory) && !string.IsNullOrEmpty(directory) ? directory : null;
        _keyFile = keyFile;
        _linkedInDirectory = string.IsNullOrEmpty(linkedInDirectory) ? null : linkedInDirectory;
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

        return read.Refusal is NotFound or LinkedInNotFound ? null : throw new CredentialRefusedException(read.Refusal!);
    }

    /// <summary>Writes <c>linkedin/token</c> (a 0600 temporary file renamed in the 0700 directory); every other name is read-only.</summary>
    public Task SetAsync(TenantId tenant, SecretName name, ReadOnlyMemory<byte> value, CancellationToken cancellationToken)
    {
        var path = WritableTokenPath(tenant, name, cancellationToken);
        if (value.IsEmpty)
        {
            throw new ArgumentException("A secret value must not be empty.", nameof(value));
        }

        var bytes = value.ToArray();
        try
        {
            M365.StateFiles.WriteAtomically(_linkedInDirectory!, path, bytes);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
        }

        return Task.CompletedTask;
    }

    /// <summary>Deletes <c>linkedin/token</c> (the revoke path); every other name is read-only.</summary>
    public Task<bool> RemoveAsync(TenantId tenant, SecretName name, CancellationToken cancellationToken)
    {
        var path = WritableTokenPath(tenant, name, cancellationToken);
        var existed = File.Exists(path);
        File.Delete(path);
        return Task.FromResult(existed);
    }

    private const string NotFound = "key: not found in credentials directory or file";
    private const string LinkedInNotFound = "linkedin: not found";

    /// <summary>Reads a credential with the shell's location order and checks.</summary>
    public Task<CredentialRead> ReadAsync(TenantId tenant, SecretName name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(name);
        cancellationToken.ThrowIfCancellationRequested();
        var prefix = IsLinkedIn(name) ? "linkedin" : "key";
        if (tenant != _tenant)
        {
            return Task.FromResult(Refuse($"{prefix}: tenant {tenant.Value} is not this instance's"));
        }

        if (_checkOwnership && !OperatingSystem.IsLinux())
        {
            return Task.FromResult(Refuse($"{prefix}: not supported on this platform"));
        }

        if (IsLinkedIn(name))
        {
            return Task.FromResult(ReadLinkedIn(name));
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

        return Task.FromResult(Check(path, source, "key", PemContent, trimNewline: false));
    }

    private static bool IsLinkedIn(SecretName name) => name.Value is LinkedInClientSecretName or LinkedInTokenName;

    private CredentialRead ReadLinkedIn(SecretName name)
    {
        if (_linkedInDirectory is null)
        {
            return Refuse($"linkedin: no credential named {name.Value}");
        }

        if (name.Value == LinkedInTokenName)
        {
            var token = Path.Join(_linkedInDirectory, LinkedInTokenFile);
            return Exists(token) ? Check(token, CredentialSource.File, "linkedin", static (_, _) => null, trimNewline: false) : Refuse(LinkedInNotFound);
        }

        if (_credentialsDirectory is not null && Exists(Path.Join(_credentialsDirectory, LinkedInCredentialsFile)))
        {
            return Check(Path.Join(_credentialsDirectory, LinkedInCredentialsFile), CredentialSource.CredentialsDirectory, "linkedin", OneLine, trimNewline: true);
        }

        var secret = Path.Join(_linkedInDirectory, LinkedInClientSecretFile);
        return Exists(secret) ? Check(secret, CredentialSource.File, "linkedin", OneLine, trimNewline: true) : Refuse(LinkedInNotFound);
    }

    // The token is the only writable name; the checks before any write mirror the read's.
    private string WritableTokenPath(TenantId tenant, SecretName name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(name);
        cancellationToken.ThrowIfCancellationRequested();
        if (name.Value != LinkedInTokenName || _linkedInDirectory is null)
        {
            throw new NotSupportedException("The credential files are read-only; cert-init writes the key pair.");
        }

        if (tenant != _tenant)
        {
            throw new CredentialRefusedException($"linkedin: tenant {tenant.Value} is not this instance's");
        }

        if (_checkOwnership && !OperatingSystem.IsLinux())
        {
            throw new CredentialRefusedException("linkedin: not supported on this platform");
        }

        return Path.Join(_linkedInDirectory, LinkedInTokenFile);
    }

    // The client secret: exactly one line with one optional trailing newline, which the value does not carry.
    private static string? OneLine(string path, byte[] bytes)
    {
        var body = bytes.AsSpan(0, bytes[^1] == (byte)'\n' ? bytes.Length - 1 : bytes.Length);
        return body.Length == 0 || body.IndexOfAny((byte)'\n', (byte)'\r') >= 0 ? $"linkedin: {path} is not one line" : null;
    }

    private static string? PemContent(string path, byte[] bytes) =>
        HasPemHeader(bytes) ? null : $"key: {path} has no PEM private-key header";

    private CredentialRead Check(string path, CredentialSource source, string prefix, Func<string, byte[], string?> content, bool trimNewline)
    {
        // [ -f ] follows a link, as the shell's test does.
        if (!File.Exists(path))
        {
            return Refuse($"{prefix}: {path} is not a regular file");
        }

        if (_checkOwnership && OperatingSystem.IsLinux())
        {
            var mode = (int)File.GetUnixFileMode(path) & 0x1FF;
            // systemd's LoadCredential copy is read-only: 0400, or 0440 (group = the unit's group) on newer systemd.
            var accepted = mode == 0x180 || (source == CredentialSource.CredentialsDirectory && mode is 0x100 or 0x120);
            if (!accepted)
            {
                return Refuse($"{prefix}: {path} must be mode 0600 (is {Convert.ToString(mode, 8)})");
            }

            // The LoadCredential copy is root's (group = the unit's group, hence 0440); the key file is the service user's.
            var owned = _ownerOf(path) is { } uid && (uid == _effectiveUser() || (uid == 0 && source == CredentialSource.CredentialsDirectory));
            if (!owned)
            {
                return Refuse($"{prefix}: {path} must be owned by {Environment.UserName}");
            }
        }

        var bytes = File.ReadAllBytes(path);
        if (bytes.Length == 0)
        {
            return Refuse($"{prefix}: {path} is empty");
        }

        // m365: grep -qE -m 1 '^-----BEGIN (RSA |EC )?PRIVATE KEY-----$' (any line, only the header line is decoded); LinkedIn: one line.
        if (content(path, bytes) is { } refusal)
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
            return Refuse(refusal);
        }

        // The client secret's trailing newline is not part of it.
        if (trimNewline && bytes[^1] == (byte)'\n')
        {
            var value = bytes[..^1];
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
            return new CredentialRead(value, source, path, null);
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
