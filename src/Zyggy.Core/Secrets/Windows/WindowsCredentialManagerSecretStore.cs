using System.ComponentModel;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;

using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Secrets.Windows;

/// <summary>
/// The laptops' store (spec 09 AC-1, founding spec §8): one generic credential per secret in the current user's Windows
/// Credential Manager — target <c>zyggy/&lt;tenant&gt;/&lt;name&gt;</c>, user name <c>zyggy</c>, the lowercase hex of the bytes as the
/// password. The vault is per Windows user: a service under another account sees another vault. A vault failure is
/// <see cref="SecretStoreUnavailableException"/> with the Win32 code and text; a non-hex password is <see cref="InvalidDataException"/>
/// naming the target. Never caches, never logs, never puts a byte of a value in a message.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsCredentialManagerSecretStore : ISecretStore, IDescribedSecretStore
{
    /// <summary>The largest value stored (a generic credential's password is capped by Windows at 512 bytes of blob, 2560 in UTF-16; hex doubles the length).</summary>
    public const int MaxValueBytes = 512;

    internal const string TargetPrefix = "zyggy/";
    internal const string UserName = "zyggy";
    internal const string Comment = "zyggy secret store — managed by zyggy secret";

    private readonly ICredentialVault _vault;

    public WindowsCredentialManagerSecretStore(ICredentialVault vault)
    {
        ArgumentNullException.ThrowIfNull(vault);
        _vault = vault;
    }

    public string StoreName => SecretStoreNames.CredentialManager;

    /// <summary>The product store over the real vault of the current Windows user; constructs nothing that touches the vault.</summary>
    [SupportedOSPlatform("windows5.1.2600")]
    public static WindowsCredentialManagerSecretStore ForCurrentUser() => new(new MeziantouCredentialVault());

    internal static string Target(TenantId tenant, SecretName name) => TargetPrefix + tenant.Value + "/" + name.Value;

    // The trailing slash keeps `acme` from matching `acme-2`.
    internal static string Filter(TenantId tenant) => TargetPrefix + tenant.Value + "/*";

    public Task<ReadOnlyMemory<byte>?> GetAsync(TenantId tenant, SecretName name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(name);
        cancellationToken.ThrowIfCancellationRequested();
        string target = Target(tenant, name);
        string? password = Call(() => _vault.Read(target));
        if (password is null)
        {
            return Task.FromResult<ReadOnlyMemory<byte>?>(null);
        }

        try
        {
            return Task.FromResult<ReadOnlyMemory<byte>?>(Convert.FromHexString(password));
        }
        catch (FormatException)
        {
            throw new InvalidDataException($"Credential '{target}' does not hold a hexadecimal value.");
        }
    }

    public Task SetAsync(TenantId tenant, SecretName name, ReadOnlyMemory<byte> value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(name);
        if (value.IsEmpty)
        {
            throw new ArgumentException("A secret value must not be empty.", nameof(value));
        }

        if (value.Length > MaxValueBytes)
        {
            throw new ArgumentException($"A secret value must not be larger than {MaxValueBytes.ToString(CultureInfo.InvariantCulture)} bytes.", nameof(value));
        }

        cancellationToken.ThrowIfCancellationRequested();
        byte[] copy = value.ToArray();
        try
        {
            string hex = Convert.ToHexStringLower(copy);
            string target = Target(tenant, name);
            Call(() =>
            {
                _vault.Write(target, UserName, hex, Comment);
                return true;
            });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
        }

        return Task.CompletedTask;
    }

    public Task<bool> RemoveAsync(TenantId tenant, SecretName name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(name);
        cancellationToken.ThrowIfCancellationRequested();
        string target = Target(tenant, name);
        return Task.FromResult(Call(() => _vault.TryDelete(target)));
    }

    public Task<IReadOnlyList<SecretName>> ListAsync(TenantId tenant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        cancellationToken.ThrowIfCancellationRequested();
        string prefix = TargetPrefix + tenant.Value + "/";
        string filter = Filter(tenant);
        IReadOnlyList<string> targets = Call(() => _vault.Enumerate(filter));
        IReadOnlyList<SecretName> names = targets
            .Where(t => t.StartsWith(prefix, StringComparison.Ordinal))
            .Select(t => SecretName.TryParse(t[prefix.Length..], out SecretName? name) ? name : null)
            .OfType<SecretName>()
            .OrderBy(n => n.Value, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(names);
    }

    public ValueTask<string> SourceOfAsync(TenantId tenant, SecretName name, CancellationToken cancellationToken) =>
        ValueTask.FromResult(SecretStoreNames.CredentialManager);

    // Every vault failure is "the store cannot be used", with the code and text and never the value.
    private static T Call<T>(Func<T> call)
    {
        try
        {
            return call();
        }
        catch (Win32Exception ex)
        {
            throw new SecretStoreUnavailableException(SecretStoreNames.CredentialManager, ex.NativeErrorCode.ToString(CultureInfo.InvariantCulture) + " " + ex.Message);
        }
    }
}
