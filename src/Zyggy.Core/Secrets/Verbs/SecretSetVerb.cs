using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Logging;

using Zyggy.Core.Tenancy;
using Zyggy.Core.Verbs;

namespace Zyggy.Core.Secrets.Verbs;

/// <summary>
/// <c>zyggy secret set</c> (spec 09 AC-7): a value from a pipe, from a hidden prompt, or generated; stored through the machine's
/// store; acknowledged with its size and fingerprint. The value itself appears nowhere — not on stdout, not on stderr, not in
/// a log line — except once, as hex, when the owner asks for <c>--generate --show</c> to carry a new key to the other machines.
/// </summary>
public sealed partial class SecretSetVerb
{
    private const int MaxValueBytes = 512;
    private const string HmacPrefix = "hmac/";

    private readonly ISecretStore _store;
    private readonly TenantId _tenant;
    private readonly ILogger<SecretSetVerb> _log;

    /// <summary>Creates the verb over <paramref name="store"/> for <paramref name="tenant"/>.</summary>
    /// <param name="store">The machine's store.</param>
    /// <param name="tenant">The tenant.</param>
    /// <param name="log">The log (one Information line per stored value, never the value).</param>
    public SecretSetVerb(ISecretStore store, TenantId tenant, ILogger<SecretSetVerb> log)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(log);
        _store = store;
        _tenant = tenant;
        _log = log;
    }

    /// <summary>Runs the verb: refusals first, then the value from the generator, the hidden prompt or standard input, then the store.</summary>
    /// <param name="request">What was asked.</param>
    /// <param name="io">The verb's console.</param>
    /// <param name="stdin">What standard input is.</param>
    /// <param name="hidden">The hidden prompt for a terminal.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The exit code (<see cref="SecretVerbExit"/>).</returns>
    public async Task<int> RunAsync(SecretSetRequest request, VerbIo io, StdinKind stdin, IHiddenInput hidden, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(io);
        ArgumentNullException.ThrowIfNull(hidden);
        string full = _tenant.Value + "/" + request.Name.Value;
        if (request.Show && !request.Generate)
        {
            await io.Error.WriteLineAsync("zyggy: --show needs --generate").ConfigureAwait(false);
            return SecretVerbExit.Usage;
        }

        if (request.Generate && !request.Name.Value.StartsWith(HmacPrefix, StringComparison.Ordinal))
        {
            await io.Error.WriteLineAsync("zyggy: --generate is for hmac/<n> keys").ConfigureAwait(false);
            return SecretVerbExit.Usage;
        }

        byte[]? value = null;
        try
        {
            if (request.Generate)
            {
                // A value piped in together with --generate is a mistake, not an input (plan 09 A9); a terminal is never read.
                if (stdin == StdinKind.Redirected && (await io.In.ReadToEndAsync(ct).ConfigureAwait(false)).Length > 0)
                {
                    await io.Error.WriteLineAsync("zyggy: --generate takes no input").ConfigureAwait(false);
                    return SecretVerbExit.Usage;
                }

                value = RandomNumberGenerator.GetBytes(32);
            }
            else
            {
                string? text = await ReadValueTextAsync(io, stdin, hidden, full, ct).ConfigureAwait(false);
                if (text is null)
                {
                    await io.Error.WriteLineAsync("zyggy: aborted; nothing stored").ConfigureAwait(false);
                    return SecretVerbExit.Aborted;
                }

                if (text.Length == 0)
                {
                    await io.Error.WriteLineAsync(stdin == StdinKind.Terminal ? "zyggy: no value entered" : "zyggy: no value on standard input").ConfigureAwait(false);
                    return SecretVerbExit.Usage;
                }

                if (request.Hex)
                {
                    try
                    {
                        value = Convert.FromHexString(text.Trim());
                    }
                    catch (FormatException)
                    {
                        await io.Error.WriteLineAsync("zyggy: value is not hexadecimal").ConfigureAwait(false);
                        return SecretVerbExit.Usage;
                    }
                }
                else
                {
                    value = Encoding.UTF8.GetBytes(text);
                }
            }

            if (value.Length == 0)
            {
                await io.Error.WriteLineAsync("zyggy: no value on standard input").ConfigureAwait(false);
                return SecretVerbExit.Usage;
            }

            if (value.Length > MaxValueBytes)
            {
                await io.Error.WriteLineAsync("zyggy: value is larger than 512 bytes").ConfigureAwait(false);
                return SecretVerbExit.Usage;
            }

            return await StoreAsync(request, io, full, value, ct).ConfigureAwait(false);
        }
        finally
        {
            if (value is not null)
            {
                CryptographicOperations.ZeroMemory(value);
            }
        }
    }

    // The value text: the whole redirected input with one trailing newline stripped, or one hidden line from the terminal.
    private static async Task<string?> ReadValueTextAsync(VerbIo io, StdinKind stdin, IHiddenInput hidden, string full, CancellationToken ct)
    {
        if (stdin == StdinKind.Terminal)
        {
            await io.Error.WriteAsync($"value for {full} (input is hidden; press Enter when done): ").ConfigureAwait(false);
            return hidden.ReadLine(ct);
        }

        string text = await io.In.ReadToEndAsync(ct).ConfigureAwait(false);
        if (text.EndsWith('\n'))
        {
            text = text[..^1];
            if (text.EndsWith('\r'))
            {
                text = text[..^1];
            }
        }

        return text;
    }

    private async Task<int> StoreAsync(SecretSetRequest request, VerbIo io, string full, byte[] value, CancellationToken ct)
    {
        string fingerprint = SecretFingerprint.Of(value);
        string? previous = null;
        try
        {
            ReadOnlyMemory<byte>? existing = await _store.GetAsync(_tenant, request.Name, ct).ConfigureAwait(false);
            if (existing is { } bytes && !bytes.IsEmpty)
            {
                previous = SecretFingerprint.Of(bytes.Span);
            }

            await _store.SetAsync(_tenant, request.Name, value, ct).ConfigureAwait(false);
        }
        catch (SecretStoreUnavailableException ex)
        {
            await io.Error.WriteLineAsync("zyggy: secret store unavailable: " + ex.Message).ConfigureAwait(false);
            return SecretVerbExit.StoreUnavailable;
        }
        catch (InvalidDataException)
        {
            // A corrupt previous value does not block the replacement; the note is simply omitted.
            await _store.SetAsync(_tenant, request.Name, value, ct).ConfigureAwait(false);
        }

        await io.Out.WriteLineAsync($"stored {full} ({value.Length.ToString(CultureInfo.InvariantCulture)} bytes) fingerprint {fingerprint}").ConfigureAwait(false);
        if (request.Show)
        {
            await io.Out.WriteLineAsync("value (hex, shown once; carry it to the other machines of the tenant): " + Convert.ToHexStringLower(value)).ConfigureAwait(false);
        }

        if (previous is not null)
        {
            await io.Error.WriteLineAsync($"note: {full} replaced (previous fingerprint {previous})").ConfigureAwait(false);
        }

        if (_store is IDescribedSecretStore described
            && await described.SourceOfAsync(_tenant, request.Name, ct).ConfigureAwait(false) == SecretStoreNames.CredentialsDirectory)
        {
            await io.Error.WriteLineAsync($"note: the credentials directory still provides {request.Name.Value} to the node").ConfigureAwait(false);
        }

        LogStored(_tenant.Value, request.Name.Value, value.Length, fingerprint);
        return SecretVerbExit.Ok;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "stored {Tenant}/{Name} ({Bytes} bytes, fingerprint {Fingerprint})")]
    private partial void LogStored(string tenant, string name, int bytes, string fingerprint);
}
