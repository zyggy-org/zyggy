using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.M365.Graph;

/// <summary>Which key pair and algorithm to mint with.</summary>
internal sealed record TokenRequest(bool UseNewKey, AssertionAlgorithm Algorithm);

/// <summary>
/// A minted token or the failure (exit 3 configuration or key, exit 6 identity). The token is never part of <see cref="ToString"/>.
/// </summary>
internal sealed class TokenResult
{
    private TokenResult(string? accessToken, DateTimeOffset? expiresAt, int exitCode, string? error, CredentialSource source)
    {
        AccessToken = accessToken;
        ExpiresAt = expiresAt;
        ExitCode = exitCode;
        Error = error;
        Source = source;
    }

    public string? AccessToken { get; }

    public DateTimeOffset? ExpiresAt { get; }

    public int ExitCode { get; }

    public string? Error { get; }

    public CredentialSource Source { get; }

    public static TokenResult Minted(string token, DateTimeOffset expiresAt, CredentialSource source) => new(token, expiresAt, 0, null, source);

    public static TokenResult Failed(int exitCode, string error, CredentialSource source = CredentialSource.None) => new(null, null, exitCode, error, source);

    public override string ToString() => ExitCode == 0 ? $"token minted, expires {ExpiresAt:O}" : $"exit {ExitCode}: {Error}";
}

/// <summary>
/// Mints a one-hour app-only token — <c>graph.sh</c>'s <c>mint_token</c> (spec 33 AC-14): the key read only through the credential
/// store, a certificate client assertion, one form POST to the tenant's token endpoint with no secret. The key buffer is cleared after
/// use; the token is held only in the returned <see cref="TokenResult"/>.
/// </summary>
/// <summary>Mints an app-only token (the headers helper's seam; <see cref="GraphTokenClient"/> is the implementation).</summary>
internal interface ITokenSource
{
    Task<TokenResult> MintAsync(TokenRequest request, CancellationToken cancellationToken);
}

internal sealed class GraphTokenClient(
    ICredentialReader store,
    TenantId tenant,
    M365Configuration configuration,
    string certificateFile,
    GraphHttp http,
    TimeProvider clock,
    Func<Guid>? newJti = null) : ITokenSource
{
    public async Task<TokenResult> MintAsync(TokenRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = SecretName.Parse(request.UseNewKey ? CredentialFileSecretStore.NewAppKeyName : CredentialFileSecretStore.AppKeyName);
        var read = await store.ReadAsync(tenant, name, cancellationToken).ConfigureAwait(false);
        if (read.Value is not { } keyBytes)
        {
            return TokenResult.Failed(3, read.Refusal!);
        }

        string assertion;
        char[]? keyChars = null;
        try
        {
            var cer = request.UseNewKey ? certificateFile + ".new" : certificateFile;
            if (!File.Exists(cer))
            {
                return TokenResult.Failed(3, $"certificate {cer} not found", read.Source);
            }

            using var certificate = X509Certificate2.CreateFromPem(File.ReadAllText(cer));
            using var rsa = RSA.Create();
            keyChars = Encoding.ASCII.GetChars(keyBytes);
            try
            {
                rsa.ImportFromPem(keyChars);
            }
            catch (Exception ex) when (ex is ArgumentException or CryptographicException)
            {
                return TokenResult.Failed(3, $"key: {read.Path} is not an RSA key", read.Source);
            }

            assertion = ClientAssertion.Build(
                rsa, certificate, request.Algorithm, Guid.Parse(configuration.TenantId), Guid.Parse(configuration.ClientId), clock.GetUtcNow(), newJti ?? Guid.NewGuid);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
            if (keyChars is not null)
            {
                Array.Clear(keyChars);
            }
        }

        var result = await http.SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, GraphEndpoints.Token(Guid.Parse(configuration.TenantId)))
            {
                Content = new FormUrlEncodedContent(
                [
                    new("grant_type", "client_credentials"),
                    new("scope", GraphEndpoints.Scope),
                    new("client_id", configuration.ClientId),
                    new("client_assertion_type", GraphEndpoints.AssertionType),
                    new("client_assertion", assertion),
                ]),
            },
            status => status is >= 400 and <= 499,
            cancellationToken).ConfigureAwait(false);
        if (result.Failure is not null)
        {
            return TokenResult.Failed(6, result.Failure, read.Source);
        }

        var response = result.Response!;
        if (!response.Accepted)
        {
            return TokenResult.Failed(6, $"token request failed ({response.Status})", read.Source);
        }

        if (response.Status != 200)
        {
            if (response.Body.Contains("AADSTS700024", StringComparison.Ordinal))
            {
                return TokenResult.Failed(6, "auth failed (clock skew, AADSTS700024) — check timedatectl on this machine", read.Source);
            }

            var error = Property(response.Body, "error");
            return TokenResult.Failed(6, $"auth failed ({error ?? $"http {response.Status}"}) — runbook 13 \"Certificate rejected\"", read.Source);
        }

        if (Property(response.Body, "access_token") is not { } token)
        {
            return TokenResult.Failed(6, "auth failed (no access_token in the response) — runbook 13 \"Certificate rejected\"", read.Source);
        }

        var expiresIn = Number(response.Body, "expires_in") ?? 3599;
        return TokenResult.Minted(token, clock.GetUtcNow().AddSeconds(expiresIn), read.Source);
    }

    private static string? Property(string body, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: > 0 } text
                    ? text
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static long? Number(string body, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
