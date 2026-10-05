using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Zyggy.Core.M365.Graph;

/// <summary>The signature algorithm of the client assertion.</summary>
internal enum AssertionAlgorithm
{
    /// <summary>RSA-PSS with SHA-256, salt length = digest length; header thumbprint <c>x5t#S256</c> (the default).</summary>
    Ps256,

    /// <summary>RSASSA-PKCS1-v1_5 with SHA-256; header thumbprint <c>x5t</c> (SHA-1).</summary>
    Rs256,
}

/// <summary>
/// The certificate client assertion exactly as <c>graph.sh</c>'s <c>build_assertion</c> (spec 33 AC-13): header <c>{alg, typ,
/// x5t#S256 | x5t}</c>, claims <c>{aud, iss, sub, jti, nbf, iat, exp = +300 s}</c> in that key order, compact JSON, base64url without
/// padding, signed with the private key.
/// </summary>
internal static class ClientAssertion
{
    private const int LifetimeSeconds = 300;

    private static readonly JsonWriterOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Indented = false };

    public static string Build(RSA key, X509Certificate2 certificate, AssertionAlgorithm algorithm, Guid tenantId, Guid clientId, DateTimeOffset now, Func<Guid> newJti)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(newJti);

        var header = Json(writer =>
        {
            if (algorithm == AssertionAlgorithm.Ps256)
            {
                writer.WriteString("alg", "PS256");
                writer.WriteString("typ", "JWT");
                writer.WriteString("x5t#S256", Base64Url(SHA256.HashData(certificate.RawData)));
            }
            else
            {
                writer.WriteString("alg", "RS256");
                writer.WriteString("typ", "JWT");
#pragma warning disable CA5350 // x5t is defined as the SHA-1 thumbprint (RFC 7515); it identifies, it does not protect.
                writer.WriteString("x5t", Base64Url(SHA1.HashData(certificate.RawData)));
#pragma warning restore CA5350
            }
        });

        var seconds = now.ToUnixTimeSeconds();
        var client = clientId.ToString("D");
        var claims = Json(writer =>
        {
            writer.WriteString("aud", GraphEndpoints.Token(tenantId));
            writer.WriteString("iss", client);
            writer.WriteString("sub", client);
            writer.WriteString("jti", Jti(newJti()));
            writer.WriteNumber("nbf", seconds);
            writer.WriteNumber("iat", seconds);
            writer.WriteNumber("exp", seconds + LifetimeSeconds);
        });

        var input = Base64Url(header) + "." + Base64Url(claims);
        var padding = algorithm == AssertionAlgorithm.Ps256 ? RSASignaturePadding.Pss : RSASignaturePadding.Pkcs1;
        var signature = key.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256, padding);
        return input + "." + Base64Url(signature);
    }

    // openssl rand -hex 16 | sed 's/^(.{8})(.{4}).(.{3}).(.{3})(.{12})$/\1-\2-4\3-8\4-\5/': UUID-v4 shaped, variant 8.
    private static string Jti(Guid random)
    {
        var hex = random.ToString("N");
        return $"{hex[..8]}-{hex[8..12]}-4{hex[13..16]}-8{hex[17..20]}-{hex[20..32]}";
    }

    private static byte[] Json(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, Compact))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
