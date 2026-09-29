using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Envelope;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Integration.Envelope;

/// <summary>
/// The composition every host uses — AddFileSecretStore().AddEnvelopeSigning() — against a key seeded on disk by hand,
/// the way an operator does it with <c>openssl rand -hex 32 &gt; &lt;root&gt;/acme/hmac/1</c>.
/// </summary>
public sealed class SignWithFileStoreTests : IDisposable
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "zyggy-it", "secrets-" + Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider _provider;
    private readonly EnvelopeSigner _signer;

    public SignWithFileStoreTests()
    {
        _provider = new ServiceCollection()
            .AddFileSecretStore(o => o.RootDirectory = _root)
            .AddEnvelopeSigning()
            .BuildServiceProvider();
        _signer = _provider.GetRequiredService<EnvelopeSigner>();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string GoldenDirectory => Path.Combine(AppContext.BaseDirectory, "golden");

    private static byte[] GoldenJob => File.ReadAllBytes(Path.Combine(GoldenDirectory, "job.md"));

    public void Dispose()
    {
        _provider.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task SignAsync_KeySeededByHandAsHexFile_ProducesGoldenSignature()
    {
        // Arrange
        SeedByHand("acme", Convert.ToHexStringLower(TestKeys.Secret1) + "\n");
        Core.Envelope.Envelope parsed = EnvelopeParser.Parse(GoldenJob, Acme).Envelope!;

        // Act
        EnvelopeResult result = await _signer.SignAsync(parsed, KeyId.Parse("acme/1"), Ct);

        // Assert
        string expected = await File.ReadAllTextAsync(Path.Combine(GoldenDirectory, "job.sig"), Ct);
        result.Envelope!.Signature.Should().Be("hmac-sha256:" + expected);
    }

    [Fact]
    public async Task ParseAndVerifyAsync_KeySeededByHand_AcceptsGoldenJob()
    {
        // Arrange
        SeedByHand("acme", Convert.ToHexStringLower(TestKeys.Secret1) + "\n");

        // Act
        EnvelopeResult result = await _signer.ParseAndVerifyAsync(GoldenJob, Acme, Ct);

        // Assert
        result.IsAccepted.Should().BeTrue(result.Rejection?.ToString());
    }

    [Fact]
    public async Task ParseAndVerifyAsync_KeySeededForOtherTenantOnly_IsUnknownKey()
    {
        // Arrange
        SeedByHand("globex", Convert.ToHexStringLower(TestKeys.Secret1) + "\n");

        // Act
        EnvelopeResult result = await _signer.ParseAndVerifyAsync(GoldenJob, Acme, Ct);

        // Assert
        result.Rejection!.Reason.Should().Be(EnvelopeRejectionReason.UnknownKey);
        result.Rejection.Field.Should().Be("key_id");
    }

    [Fact]
    public async Task ParseAndVerifyAsync_CorruptKeyFile_PropagatesInvalidDataException()
    {
        // Arrange
        SeedByHand("acme", "not-hex\n");
        Func<Task> act = () => _signer.ParseAndVerifyAsync(GoldenJob, Acme, Ct);

        // Act & Assert
        await act.Should().ThrowAsync<InvalidDataException>();
    }

    private void SeedByHand(string tenant, string content)
    {
        string directory = Path.Combine(_root, tenant, "hmac");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "1"), content);
    }
}
