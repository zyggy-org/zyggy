using System.Text;

using Zyggy.Core.Envelope;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Envelope;

public sealed class EnvelopeSignerSignTests
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(Golden.Cases), MemberType = typeof(Golden))]
    public async Task SignAsync_EveryGoldenCase_ProducesTheGoldenSignature(string name)
    {
        // Arrange
        Model parsed = Parse(Golden.Md(name));
        var signer = new EnvelopeSigner(await TestKeys.StoreWithBothAsync(Ct));

        // Act
        EnvelopeResult result = await signer.SignAsync(parsed, parsed.KeyId!, Ct);

        // Assert
        Golden.Sig(name).Should().MatchRegex("^[0-9a-f]{64}$");
        result.IsAccepted.Should().BeTrue();
        result.Envelope!.Signature.Should().Be("hmac-sha256:" + Golden.Sig(name));
        result.Envelope.Signature.Should().Be(parsed.Signature);
    }

    [Fact]
    public async Task SignAsync_UnsignedFactoryEnvelope_SetsKeyIdAndSigAndSerialisesToAParsableFile()
    {
        // Arrange
        EnvelopeHeader header = EnvelopeFactoryTests.Header(new DateTimeOffset(2026, 9, 27, 16, 5, 0, 789, TimeSpan.FromHours(2)));
        Model unsigned = Model.CreateJob(header, new JobFields("calizr"), "Do it.\n"u8.ToArray());
        var signer = new EnvelopeSigner(await TestKeys.StoreWithBothAsync(Ct));

        // Act
        EnvelopeResult signed = await signer.SignAsync(unsigned, TestKeys.Acme1, Ct);

        // Assert
        signed.IsAccepted.Should().BeTrue();
        byte[] file = EnvelopeWriter.Serialize(signed.Envelope!);
        Model reparsed = Parse(file);
        reparsed.KeyId.Should().Be(TestKeys.Acme1);
        reparsed.Signature.Should().Be(signed.Envelope!.Signature);
        reparsed.Header.Created.Should().Be(signed.Envelope.Header.Created);
        Encoding.UTF8.GetString(file).Should().Contain("\ncreated: 2026-09-27T14:05:00Z\n");
    }

    [Fact]
    public async Task SignAsync_AlreadySignedWithOtherKey_ReplacesKeyIdAndSig()
    {
        // Arrange
        Model context = Parse(Golden.Md("context"));
        var signer = new EnvelopeSigner(await TestKeys.StoreWithBothAsync(Ct));

        // Act
        EnvelopeResult result = await signer.SignAsync(context, TestKeys.Acme1, Ct);

        // Assert
        Model resigned = result.Envelope!;
        resigned.KeyId.Should().Be(TestKeys.Acme1);
        resigned.FrontMatter.Entries["key_id"].Should().Be(new FrontMatterScalar("acme/1"));
        resigned.Signature.Should().NotBe(context.Signature);
        string before = Encoding.UTF8.GetString(EnvelopeSigner.Canonicalize(context));
        string after = Encoding.UTF8.GetString(EnvelopeSigner.Canonicalize(resigned));
        after.Should().Be(before.Replace("key_id: acme/2", "key_id: acme/1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SignAsync_KeyAbsent_ReturnsUnknownKeyWithDetailAbsent()
    {
        // Arrange
        Model job = Parse(Golden.Md("job"));
        var signer = new EnvelopeSigner(await TestKeys.StoreWithAsync(TestKeys.Acme2, Ct));

        // Act
        EnvelopeResult result = await signer.SignAsync(job, TestKeys.Acme1, Ct);

        // Assert
        result.Rejection!.Reason.Should().Be(EnvelopeRejectionReason.UnknownKey);
        result.Rejection.Field.Should().Be("key_id");
        result.Rejection.Detail.Should().Contain("absent");
    }

    [Fact]
    public async Task SignAsync_KeyShorterThan32Bytes_ReturnsUnknownKeyWithDetailShort()
    {
        // Arrange
        byte[] shortKey = Encoding.UTF8.GetBytes("zyggy-short-test-key-acme-00031");
        var store = new InMemorySecretStore();
        await store.SetAsync(Acme, TestKeys.Acme1.SecretName, shortKey, Ct);
        var signer = new EnvelopeSigner(store);

        // Act
        EnvelopeResult result = await signer.SignAsync(Parse(Golden.Md("job")), TestKeys.Acme1, Ct);

        // Assert
        shortKey.Should().HaveCount(31);
        result.Rejection!.Reason.Should().Be(EnvelopeRejectionReason.UnknownKey);
        result.Rejection.Detail.Should().Contain("shorter than 32 bytes");
        result.Rejection.Detail.Should().NotContain("zyggy-short").And.NotContain(Convert.ToHexStringLower(shortKey)[..16]);
    }

    [Fact]
    public async Task SignAsync_KeyOfOtherTenant_ThrowsArgumentException()
    {
        // Arrange
        var signer = new EnvelopeSigner(await TestKeys.StoreWithBothAsync(Ct));
        Func<Task> act = () => signer.SignAsync(Parse(Golden.Md("job")), KeyId.Parse("globex/1"), Ct);

        // Act & Assert
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task SignAsync_DoesNotMutateInput()
    {
        // Arrange
        Model context = Parse(Golden.Md("context"));
        string? signature = context.Signature;
        FrontMatterMapping frontMatter = context.FrontMatter;
        var signer = new EnvelopeSigner(await TestKeys.StoreWithBothAsync(Ct));

        // Act
        await signer.SignAsync(context, TestKeys.Acme1, Ct);

        // Assert
        context.Signature.Should().Be(signature);
        context.FrontMatter.Should().BeSameAs(frontMatter);
        context.KeyId.Should().Be(TestKeys.Acme2);
    }

    [Fact]
    public async Task SignAsync_Cancelled_ThrowsOperationCanceledException()
    {
        // Arrange
        var signer = new EnvelopeSigner(await TestKeys.StoreWithBothAsync(Ct));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        Func<Task> act = () => signer.SignAsync(Parse(Golden.Md("job")), TestKeys.Acme1, cancelled.Token);

        // Act & Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void Constructor_NullStore_ThrowsArgumentNullException()
    {
        // Arrange
        Action act = () => _ = new EnvelopeSigner(null!);

        // Act & Assert
        act.Should().Throw<ArgumentNullException>();
    }

    private static Model Parse(byte[] file)
    {
        EnvelopeResult result = EnvelopeParser.Parse(file, Acme);
        result.IsAccepted.Should().BeTrue(result.Rejection?.ToString());
        return result.Envelope!;
    }
}
