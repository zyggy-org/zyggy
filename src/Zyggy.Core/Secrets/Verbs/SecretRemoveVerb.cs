using Microsoft.Extensions.Logging;

using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Verbs;

namespace Zyggy.Core.Secrets.Verbs;

/// <summary>
/// <c>zyggy secret remove</c> (spec 09 AC-8; <c>rotate-hmac.md</c>'s compromise variant): removes one name from this machine's
/// store — never the key <c>signWith</c> names, which would stop the machine signing.
/// </summary>
public sealed partial class SecretRemoveVerb
{
    private readonly ISecretStore _store;
    private readonly TenantId _tenant;
    private readonly KeyId? _signWith;
    private readonly ILogger<SecretRemoveVerb> _log;

    /// <summary>Creates the verb over <paramref name="store"/> for <paramref name="tenant"/>.</summary>
    /// <param name="store">The machine's store.</param>
    /// <param name="tenant">The tenant.</param>
    /// <param name="signWith">The key this machine signs with, or null.</param>
    /// <param name="log">The log.</param>
    public SecretRemoveVerb(ISecretStore store, TenantId tenant, KeyId? signWith, ILogger<SecretRemoveVerb> log)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(log);
        _store = store;
        _tenant = tenant;
        _signWith = signWith;
        _log = log;
    }

    /// <summary>Removes <paramref name="name"/> unless it is the signing key.</summary>
    /// <param name="name">The tenant-relative name.</param>
    /// <param name="io">The verb's console.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The exit code (<see cref="SecretVerbExit"/>).</returns>
    public async Task<int> RunAsync(SecretName name, VerbIo io, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(io);
        string full = _tenant.Value + "/" + name.Value;
        if (_signWith is { } signWith && signWith.SecretName == name)
        {
            await io.Error.WriteLineAsync($"zyggy: {full} is this machine's signing key (signWith); change signWith first").ConfigureAwait(false);
            return SecretVerbExit.Usage;
        }

        bool removed;
        try
        {
            removed = await _store.RemoveAsync(_tenant, name, ct).ConfigureAwait(false);
        }
        catch (SecretStoreUnavailableException ex)
        {
            await io.Error.WriteLineAsync("zyggy: secret store unavailable: " + ex.Message).ConfigureAwait(false);
            return SecretVerbExit.StoreUnavailable;
        }

        if (!removed)
        {
            await io.Error.WriteLineAsync($"zyggy: {full} is not in the {SecretStoreNames.Of(_store)}").ConfigureAwait(false);
            return SecretVerbExit.NotPresent;
        }

        await io.Out.WriteLineAsync("removed " + full).ConfigureAwait(false);
        LogRemoved(_tenant.Value, name.Value);
        return SecretVerbExit.Ok;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "removed {Tenant}/{Name}")]
    private partial void LogRemoved(string tenant, string name);
}
