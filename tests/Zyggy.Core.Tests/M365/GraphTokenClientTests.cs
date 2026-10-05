using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Web;

using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.M365;
using Zyggy.Core.M365.Graph;
using Zyggy.Core.Memory;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.M365;

/// <summary>
/// The app-only token request (spec 33 AC-14): one POST to the tenant's token endpoint with a certificate assertion and no secret;
/// the shell's message for every auth failure (stubbed login host).
/// </summary>
public sealed class GraphTokenClientTests : IDisposable
{
    private const string TokenRoute = "/oauth2/v2\\.0/token$";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "zyggy-ut", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero)) { AutoAdvanceAmount = TimeSpan.Zero };
    private readonly StubGraphHandler _stub = StubGraphHandler.FromGoldenRoutes();
    private readonly TestCertificates _pair;
    private readonly M365Configuration _configuration;

    public GraphTokenClientTests()
    {
        _pair = TestCertificates.Create(Path.Combine(_root, "config", "zyggy"));
        var config = Path.Combine(_root, "m365.json");
        File.Copy(M365Run.Golden("fixtures", "m365.json"), config);
        _configuration = M365Configuration.Load(config, baseOnly: false, new DateOnly(2026, 9, 30), _ => true).Configuration!;
    }

    public void Dispose()
    {
        _pair.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Mint_OnePostToTenantTokenEndpoint_FormFieldsExactNoSecret()
    {
        // Act
        var result = await Client().MintAsync(new TokenRequest(UseNewKey: false, AssertionAlgorithm.Ps256), CancellationToken.None);

        // Assert
        result.ExitCode.Should().Be(0, result.Error);
        result.Source.Should().Be(CredentialSource.File);
        var request = _stub.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.ToString().Should().Be("https://login.microsoftonline.com/11111111-1111-4111-8111-111111111111/oauth2/v2.0/token");
        var form = HttpUtility.ParseQueryString(request.Body!);
        form.AllKeys.Should().Equal("grant_type", "scope", "client_id", "client_assertion_type", "client_assertion");
        form["grant_type"].Should().Be("client_credentials");
        form["scope"].Should().Be("https://graph.microsoft.com/.default");
        form["client_id"].Should().Be("22222222-2222-4222-8222-222222222222");
        form["client_assertion_type"].Should().Be("urn:ietf:params:oauth:client-assertion-type:jwt-bearer");
        form["client_assertion"]!.Split('.').Should().HaveCount(3);
        request.Body.Should().NotContain("client_secret");
        _stub.Violations.Should().BeEmpty();
    }

    [Fact]
    public async Task Mint_Ok_TokenAndExpiryFromExpiresIn()
    {
        // Act
        var result = await Client().MintAsync(new TokenRequest(false, AssertionAlgorithm.Ps256), CancellationToken.None);

        // Assert
        result.AccessToken.Should().StartWith("eyJSTUBACCESS.");
        result.ExpiresAt.Should().Be(_clock.GetUtcNow().AddSeconds(3599));
        result.ToString().Should().NotContain("eyJSTUBACCESS");
    }

    [Fact]
    public async Task Mint_ClockSkew_ExitSixNamesTimedatectl()
    {
        // Arrange
        _stub.Once("POST", TokenRoute, 401, File.ReadAllText(StubGraphHandler.GraphFixture("token-clock-skew.json")));

        // Act
        var result = await Client().MintAsync(new TokenRequest(false, AssertionAlgorithm.Ps256), CancellationToken.None);

        // Assert
        result.ExitCode.Should().Be(6);
        result.Error.Should().Be("auth failed (clock skew, AADSTS700024) — check timedatectl on this machine");
    }

    [Theory]
    [InlineData("token-invalid-client.json", "invalid_client")]
    [InlineData("token-unauthorized-client.json", "unauthorized_client")]
    public async Task Mint_Other4xx_ExitSixRunbookCertificateRejected(string fixture, string error)
    {
        // Arrange
        _stub.Once("POST", TokenRoute, 400, File.ReadAllText(StubGraphHandler.GraphFixture(fixture)));

        // Act
        var result = await Client().MintAsync(new TokenRequest(false, AssertionAlgorithm.Ps256), CancellationToken.None);

        // Assert
        result.ExitCode.Should().Be(6);
        result.Error.Should().Be($"auth failed ({error}) — runbook 13 \"Certificate rejected\"");
    }

    [Fact]
    public async Task Mint_4xxWithoutError_HttpStatusNamed()
    {
        // Arrange
        _stub.Once("POST", TokenRoute, 400, "not json");

        // Act
        var result = await Client().MintAsync(new TokenRequest(false, AssertionAlgorithm.Ps256), CancellationToken.None);

        // Assert
        result.Error.Should().Be("auth failed (http 400) — runbook 13 \"Certificate rejected\"");
    }

    [Fact]
    public async Task Mint_500_TokenRequestFailed()
    {
        // Arrange
        _stub.Once("POST", TokenRoute, 500, "{}");

        // Act
        var result = await Client().MintAsync(new TokenRequest(false, AssertionAlgorithm.Ps256), CancellationToken.None);

        // Assert
        result.ExitCode.Should().Be(6);
        result.Error.Should().Be("token request failed (500)");
    }

    [Fact]
    public async Task Mint_NoAccessToken_ExitSix()
    {
        // Arrange
        _stub.Once("POST", TokenRoute, 200, """{"token_type":"Bearer"}""");

        // Act
        var result = await Client().MintAsync(new TokenRequest(false, AssertionAlgorithm.Ps256), CancellationToken.None);

        // Assert
        result.ExitCode.Should().Be(6);
        result.Error.Should().Be("auth failed (no access_token in the response) — runbook 13 \"Certificate rejected\"");
    }

    [Fact]
    public async Task Mint_KeyFileMtimeUnchanged()
    {
        // Arrange
        var before = File.GetLastWriteTimeUtc(_pair.KeyFile);

        // Act
        await Client().MintAsync(new TokenRequest(false, AssertionAlgorithm.Ps256), CancellationToken.None);

        // Assert
        File.GetLastWriteTimeUtc(_pair.KeyFile).Should().Be(before);
    }

    [Fact]
    public async Task Mint_Pkcs1Key_Works()
    {
        // Arrange
        using var pkcs1 = TestCertificates.Create(Path.Combine(_root, "config", "zyggy"), pkcs1: true);

        // Act
        var result = await Client().MintAsync(new TokenRequest(false, AssertionAlgorithm.Rs256), CancellationToken.None);

        // Assert
        result.ExitCode.Should().Be(0, result.Error);
    }

    [Fact]
    public async Task Mint_EcKey_ExitThree()
    {
        // Arrange
        TestCertificates.WriteEcKey(_pair.KeyFile);

        // Act
        var result = await Client().MintAsync(new TokenRequest(false, AssertionAlgorithm.Ps256), CancellationToken.None);

        // Assert
        result.ExitCode.Should().Be(3);
        result.Error.Should().Be($"key: {_pair.KeyFile} is not an RSA key");
        _stub.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Mint_KeyMissing_ExitThreeShellText()
    {
        // Arrange
        File.Delete(_pair.KeyFile);

        // Act
        var result = await Client().MintAsync(new TokenRequest(false, AssertionAlgorithm.Ps256), CancellationToken.None);

        // Assert
        result.ExitCode.Should().Be(3);
        result.Error.Should().Be("key: not found in credentials directory or file");
    }

    [Fact]
    public async Task Mint_CertificateMissing_ExitThree()
    {
        // Arrange
        File.Delete(_pair.CertificateFile);

        // Act
        var result = await Client().MintAsync(new TokenRequest(false, AssertionAlgorithm.Ps256), CancellationToken.None);

        // Assert
        result.ExitCode.Should().Be(3);
        result.Error.Should().Be($"certificate {_pair.CertificateFile} not found");
    }

    [Fact]
    public async Task Mint_NewKey_UsesDotNewPair()
    {
        // Arrange
        using var next = TestCertificates.Create(Path.Combine(_root, "next"));
        File.Copy(next.KeyFile, _pair.KeyFile + ".new");
        File.Copy(next.CertificateFile, _pair.CertificateFile + ".new");

        // Act
        var result = await Client().MintAsync(new TokenRequest(UseNewKey: true, AssertionAlgorithm.Ps256), CancellationToken.None);

        // Assert
        result.ExitCode.Should().Be(0, result.Error);
        var assertion = HttpUtility.ParseQueryString(_stub.Requests.Single().Body!)["client_assertion"]!.Split('.');
        using var publicKey = next.Certificate.GetRSAPublicKey()!;
        publicKey.VerifyData(Encoding.ASCII.GetBytes(assertion[0] + "." + assertion[1]), FromBase64Url(assertion[2]),
            System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pss).Should().BeTrue();
    }

    [Fact]
    public async Task Mint_KeyBufferCleared()
    {
        // Arrange
        var store = new RecordingStore(new CredentialFileSecretStore(TenantId.Parse("acme"), Env(), _pair.KeyFile, checkOwnership: false));

        // Act
        await Client(store).MintAsync(new TokenRequest(false, AssertionAlgorithm.Ps256), CancellationToken.None);

        // Assert
        store.LastValue.Should().NotBeNull();
        store.LastValue!.Should().OnlyContain(b => b == 0);
    }

    [Fact]
    public async Task Mint_TransportErrorWithSecretText_Withheld()
    {
        // Arrange
        _stub.OnceThrow("POST", TokenRoute, "proxy said password: hunter2secret\nmore");

        // Act
        var result = await Client().MintAsync(new TokenRequest(false, AssertionAlgorithm.Ps256), CancellationToken.None);

        // Assert
        result.ExitCode.Should().Be(6);
        result.Error.Should().Be("request failed (curl error text withheld: matches secret pattern credential-assignment)");
    }

    [Fact]
    public void Http_HttpUri_Refused()
    {
        // Arrange
        using var http = Http();

        // Act
        var act = () => http.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "http://graph.microsoft.com/v1.0/drives"), _ => false, CancellationToken.None);

        // Assert
        act.Should().ThrowAsync<InvalidOperationException>();
    }

    private static byte[] FromBase64Url(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded + new string('=', (4 - (padded.Length % 4)) % 4));
    }

    private static Dictionary<string, string?> Env() => new(StringComparer.Ordinal);

    private GraphHttp Http() => new(_stub, _clock, SecretPatterns.Load(Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt")).Patterns!);

    private GraphTokenClient Client(ICredentialReader? store = null) => new(
        store ?? new CredentialFileSecretStore(TenantId.Parse("acme"), Env(), _pair.KeyFile, checkOwnership: false),
        TenantId.Parse("acme"),
        _configuration,
        _pair.CertificateFile,
        Http(),
        _clock);

    private sealed class RecordingStore(CredentialFileSecretStore inner) : ICredentialReader
    {
        public byte[]? LastValue { get; private set; }

        public async Task<CredentialRead> ReadAsync(TenantId tenant, SecretName name, CancellationToken cancellationToken)
        {
            var read = await inner.ReadAsync(tenant, name, cancellationToken);
            LastValue = read.Value;
            return read;
        }
    }
}
