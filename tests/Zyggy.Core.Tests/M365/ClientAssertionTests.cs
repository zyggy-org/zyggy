using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Zyggy.Core.M365.Graph;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.M365;

/// <summary>
/// The certificate client assertion exactly as <c>graph.sh</c> builds it (spec 33 AC-13): header <c>{alg, typ, x5t#S256 | x5t}</c>,
/// claims <c>{aud, iss, sub, jti, nbf, iat, exp = +300 s}</c>, PS256 (PSS, salt = digest length) or RS256, base64url without padding.
/// </summary>
public sealed partial class ClientAssertionTests : IDisposable
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Client = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 10, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "zyggy-ut", Guid.NewGuid().ToString("N"));
    private readonly TestCertificates _pair;

    public ClientAssertionTests() => _pair = TestCertificates.Create(_directory);

    public void Dispose()
    {
        _pair.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Build_Ps256_HeaderExactAndThumbprintSha256OfDer()
    {
        // Act
        var parts = Build(AssertionAlgorithm.Ps256).Split('.');

        // Assert
        var thumbprint = Base64Url(SHA256.HashData(_pair.Certificate.RawData));
        Decode(parts[0]).Should().Be($$"""{"alg":"PS256","typ":"JWT","x5t#S256":"{{thumbprint}}"}""");
    }

    [Fact]
    public void Build_Rs256_HeaderExactAndThumbprintSha1OfDer()
    {
        // Act
        var parts = Build(AssertionAlgorithm.Rs256).Split('.');

        // Assert
#pragma warning disable CA5350 // x5t is the SHA-1 thumbprint by definition (RFC 7515)
        var thumbprint = Base64Url(SHA1.HashData(_pair.Certificate.RawData));
#pragma warning restore CA5350
        Decode(parts[0]).Should().Be($$"""{"alg":"RS256","typ":"JWT","x5t":"{{thumbprint}}"}""");
    }

    [Fact]
    public void Build_Claims_AudIssSubJtiNbfIatExp300()
    {
        // Act
        var parts = Build(AssertionAlgorithm.Ps256, () => Guid.Parse("0123abcd-4567-4890-8abc-def012345678")).Split('.');

        // Assert
        const long now = 1790762400;
        Decode(parts[1]).Should().Be(
            "{\"aud\":\"https://login.microsoftonline.com/11111111-1111-4111-8111-111111111111/oauth2/v2.0/token\"," +
            "\"iss\":\"22222222-2222-4222-8222-222222222222\",\"sub\":\"22222222-2222-4222-8222-222222222222\"," +
            $"\"jti\":\"0123abcd-4567-4890-8abc-def012345678\",\"nbf\":{now},\"iat\":{now},\"exp\":{now + 300}}}");
    }

    [Fact]
    public void Build_Jti_UuidV4Shaped()
    {
        // Act
        var claims = JsonDocument.Parse(Decode(Build(AssertionAlgorithm.Ps256).Split('.')[1])).RootElement;

        // Assert
        claims.GetProperty("jti").GetString().Should().MatchRegex("^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-8[0-9a-f]{3}-[0-9a-f]{12}$");
    }

    [Fact]
    public void Build_NoPadding_Base64UrlAlphabetOnly()
    {
        // Act
        var assertion = Build(AssertionAlgorithm.Ps256);

        // Assert
        Base64UrlParts().IsMatch(assertion).Should().BeTrue();
    }

    [Fact]
    public void Build_Ps256_SignatureVerifiesWithCertificatePssSaltDigestLength()
    {
        // Act
        var parts = Build(AssertionAlgorithm.Ps256).Split('.');

        // Assert
        using var publicKey = _pair.Certificate.GetRSAPublicKey()!;
        publicKey.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), FromBase64Url(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pss)
            .Should().BeTrue();
    }

    [Fact]
    public void Build_Rs256_SignatureVerifiesPkcs1()
    {
        // Act
        var parts = Build(AssertionAlgorithm.Rs256).Split('.');

        // Assert
        using var publicKey = _pair.Certificate.GetRSAPublicKey()!;
        publicKey.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), FromBase64Url(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .Should().BeTrue();
    }

    [Fact]
    public void Build_TamperedPayload_DoesNotVerify()
    {
        // Arrange
        var parts = Build(AssertionAlgorithm.Ps256).Split('.');
        var tampered = Base64Url(Encoding.UTF8.GetBytes(Decode(parts[1]).Replace("\"exp\":", "\"exp\":9", StringComparison.Ordinal)));

        // Assert
        using var publicKey = _pair.Certificate.GetRSAPublicKey()!;
        publicKey.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + tampered), FromBase64Url(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pss)
            .Should().BeFalse();
    }

    private string Build(AssertionAlgorithm algorithm, Func<Guid>? jti = null) =>
        ClientAssertion.Build(_pair.Key, _pair.Certificate, algorithm, Tenant, Client, Now, jti ?? Guid.NewGuid);

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded + new string('=', (4 - (padded.Length % 4)) % 4));
    }

    private static string Decode(string part) => Encoding.UTF8.GetString(FromBase64Url(part));

    [GeneratedRegex(@"^[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$")]
    private static partial Regex Base64UrlParts();
}
