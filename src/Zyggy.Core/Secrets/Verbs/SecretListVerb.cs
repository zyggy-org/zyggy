using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Verbs;

namespace Zyggy.Core.Secrets.Verbs;

/// <summary>
/// <c>zyggy secret list</c> (spec 09 AC-8): the names this machine holds for its tenant with size, fingerprint, the layer that
/// provides each and the key's role (<c>signs</c> for <c>signWith</c>, <c>verify-only</c> for any other <c>hmac/&lt;n&gt;</c>). Never a value.
/// </summary>
public sealed class SecretListVerb
{
    private const string HmacPrefix = "hmac/";
    private static readonly string[] Header = ["name", "bytes", "fingerprint", "store", "role"];

    private readonly ISecretStore _store;
    private readonly TenantId _tenant;
    private readonly KeyId? _signWith;

    /// <summary>Creates the verb over <paramref name="store"/> for <paramref name="tenant"/>.</summary>
    /// <param name="store">The machine's store.</param>
    /// <param name="tenant">The tenant.</param>
    /// <param name="signWith">The key this machine signs with, or null.</param>
    public SecretListVerb(ISecretStore store, TenantId tenant, KeyId? signWith)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(tenant);
        _store = store;
        _tenant = tenant;
        _signWith = signWith;
    }

    /// <summary>Prints the table, or <c>(none)</c>.</summary>
    /// <param name="io">The verb's console.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The exit code (<see cref="SecretVerbExit"/>).</returns>
    public async Task<int> RunAsync(VerbIo io, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(io);
        List<string[]> rows;
        try
        {
            rows = await RowsAsync(ct).ConfigureAwait(false);
        }
        catch (SecretStoreUnavailableException ex)
        {
            await io.Error.WriteLineAsync("zyggy: secret store unavailable: " + ex.Message).ConfigureAwait(false);
            return SecretVerbExit.StoreUnavailable;
        }

        if (rows.Count == 0)
        {
            await io.Out.WriteLineAsync("(none)").ConfigureAwait(false);
            return SecretVerbExit.Ok;
        }

        // Columns padded to the widest value, two spaces between them; the last column as is (plan 09 A10).
        int[] widths = new int[Header.Length - 1];
        foreach (string[] row in rows.Prepend(Header))
        {
            for (int i = 0; i < widths.Length; i++)
            {
                widths[i] = Math.Max(widths[i], row[i].Length);
            }
        }

        foreach (string[] row in rows.Prepend(Header))
        {
            var line = new StringBuilder();
            for (int i = 0; i < widths.Length; i++)
            {
                line.Append(row[i].PadRight(widths[i])).Append("  ");
            }

            line.Append(row[^1]);
            await io.Out.WriteLineAsync(line.ToString()).ConfigureAwait(false);
        }

        return SecretVerbExit.Ok;
    }

    private async Task<List<string[]>> RowsAsync(CancellationToken ct)
    {
        var rows = new List<string[]>();
        foreach (SecretName name in await _store.ListAsync(_tenant, ct).ConfigureAwait(false))
        {
            ReadOnlyMemory<byte>? value = await _store.GetAsync(_tenant, name, ct).ConfigureAwait(false);
            string bytes = "?";
            string fingerprint = "?";
            if (value is { } present && !present.IsEmpty)
            {
                byte[] copy = present.ToArray();
                try
                {
                    bytes = copy.Length.ToString(CultureInfo.InvariantCulture);
                    fingerprint = SecretFingerprint.Of(copy);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(copy);
                }
            }

            string source = _store is IDescribedSecretStore described ? await described.SourceOfAsync(_tenant, name, ct).ConfigureAwait(false) : SecretStoreNames.Of(_store);
            string role = _signWith is { } signWith && signWith.SecretName == name ? "signs"
                : name.Value.StartsWith(HmacPrefix, StringComparison.Ordinal) ? "verify-only"
                : string.Empty;
            rows.Add([name.Value, bytes, fingerprint, source, role]);
        }

        return rows;
    }
}
