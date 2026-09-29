using System.Text;

using NSubstitute;

using Zyggy.Core.Envelope;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Envelope;

public sealed class EnvelopeSignerVerifyTests
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static EnvelopeText Job => EnvelopeText.From(Golden.Md("job"));

    /// <summary>Every top-level key of golden/job.md except sig, with a different well-formed value and the expected outcome.</summary>
    public static TheoryData<string, string, EnvelopeRejectionReason> KeyMutations => new()
    {
        { "agent", "other-agent", EnvelopeRejectionReason.InvalidSignature },
        { "allowed_tools", "[Read]", EnvelopeRejectionReason.InvalidSignature },
        { "attempt", "2", EnvelopeRejectionReason.InvalidSignature },
        { "created", "2026-09-27T14:05:01Z", EnvelopeRejectionReason.InvalidSignature },
        { "deadline", "2026-09-29T18:00:01Z", EnvelopeRejectionReason.InvalidSignature },
        { "from", "home-laptop", EnvelopeRejectionReason.InvalidSignature },
        { "id", "01J8Y3N7Q2X9Z4A5B6C7D8E9F2", EnvelopeRejectionReason.InvalidSignature },
        { "key_id", "acme/3", EnvelopeRejectionReason.UnknownKey },
        { "priority", "high", EnvelopeRejectionReason.InvalidSignature },
        { "project", "other", EnvelopeRejectionReason.InvalidSignature },
        { "report_back", "[summary]", EnvelopeRejectionReason.InvalidSignature },
        { "schema", "2", EnvelopeRejectionReason.SchemaUnsupported },
        { "tenant", "globex", EnvelopeRejectionReason.TenantMismatch },
        { "timeout_minutes", "31", EnvelopeRejectionReason.InvalidSignature },
        { "to", "central", EnvelopeRejectionReason.InvalidSignature },
        { "type", "context", EnvelopeRejectionReason.MissingField },
        { "worktree", "false", EnvelopeRejectionReason.InvalidSignature },
    };

    public static TheoryData<int> BodyOffsets()
    {
        var data = new TheoryData<int>();
        int length = Parse(Golden.Md("job")).Body.Length;
        for (int i = 0; i < length; i++)
        {
            data.Add(i);
        }

        return data;
    }

    public static TheoryData<string> StructuralTampers => new() { "field added", "field removed", "body byte appended", "final newline removed" };

    [Theory]
    [MemberData(nameof(Golden.Cases), MemberType = typeof(Golden))]
    public async Task ParseAndVerifyAsync_EveryGoldenCase_IsAccepted(string name)
    {
        // Arrange
        var signer = new EnvelopeSigner(await TestKeys.StoreWithBothAsync(Ct));

        // Act
        EnvelopeResult result = await signer.ParseAndVerifyAsync(Golden.Md(name), Acme, Ct);

        // Assert
        result.Rejection.Should().BeNull();
        result.IsAccepted.Should().BeTrue();
    }

    [Fact]
    public void KeyMutations_CoverEveryTopLevelKeyOfGoldenJobExceptSig()
    {
        // Act
        IEnumerable<string> keys = Parse(Golden.Md("job")).FrontMatter.Entries.Keys.Where(k => k != "sig");

        // Assert
        KeyMutations.Select(row => row.Data.Item1).Should().BeEquivalentTo(keys);
    }

    [Theory]
    [MemberData(nameof(KeyMutations))]
    public async Task ParseAndVerifyAsync_TopLevelKeyMutated_IsRejectedWithExpectedReason(
        string key, string value, EnvelopeRejectionReason expected)
    {
        // Arrange
        var signer = new EnvelopeSigner(await TestKeys.StoreWithBothAsync(Ct));

        // Act
        EnvelopeResult result = await signer.ParseAndVerifyAsync(Job.WithValue(key, value).ToBytes(), Acme, Ct);

        // Assert
        result.IsAccepted.Should().BeFalse();
        result.Rejection!.Reason.Should().Be(expected);
    }

    [Fact]
    public async Task ParseAndVerifyAsync_SigMutated_IsInvalidSignature()
    {
        // Arrange
        string sig = Golden.Sig("job");
        string mutated = (sig[0] == '0' ? "1" : "0") + sig[1..];
        var signer = new EnvelopeSigner(await TestKeys.StoreWithBothAsync(Ct));

        // Act
        EnvelopeResult result = await signer.ParseAndVerifyAsync(Job.WithValue("sig", "hmac-sha256:" + mutated).ToBytes(), Acme, Ct);

        // Assert
        result.Rejection!.Reason.Should().Be(EnvelopeRejectionReason.InvalidSignature);
        result.Rejection.Field.Should().Be("sig");
    }

    [Theory]
    [MemberData(nameof(BodyOffsets))]
    public async Task ParseAndVerifyAsync_AnyBodyByteFlipped_IsInvalidSignature(int offset)
    {
        // Arrange
        byte[] body = Parse(Golden.Md("job")).Body.ToArray();
        body[offset] ^= 0x01;
        var signer = new EnvelopeSigner(await TestKeys.StoreWithBothAsync(Ct));

        // Act
        EnvelopeResult result = await signer.ParseAndVerifyAsync(Job.WithBody(body).ToBytes(), Acme, Ct);

        // Assert
        result.Rejection!.Reason.Should().Be(EnvelopeRejectionReason.InvalidSignature);
    }

    [Theory]
    [MemberData(nameof(StructuralTampers))]
    public async Task ParseAndVerifyAsync_StructuralTamper_IsInvalidSignature(string tamper)
    {
        // Arrange
        byte[] body = Parse(Golden.Md("job")).Body.ToArray();
        byte[] file = tamper switch
        {
            "field added" => Job.WithKey("x_added", "1").ToBytes(),
            "field removed" => Job.WithoutKey("agent").ToBytes(),
            "body byte appended" => Job.WithBody([.. body, (byte)'x']).ToBytes(),
            _ => Job.WithBody(body[..^1]).ToBytes(),
        };
        var signer = new EnvelopeSigner(await TestKeys.StoreWithBothAsync(Ct));

        // Act
        EnvelopeResult result = await signer.ParseAndVerifyAsync(file, Acme, Ct);

        // Assert
        result.Rejection!.Reason.Should().Be(EnvelopeRejectionReason.InvalidSignature);
    }

    [Fact]
    public async Task ParseAndVerifyAsync_ForeignTenant_IsTenantMismatchAndStoreNeverCalled()
    {
        // Arrange
        ISecretStore store = Substitute.For<ISecretStore>();
        var signer = new EnvelopeSigner(store);
        byte[] file = Job.WithValue("tenant", "globex").WithValue("key_id", "globex/1").ToBytes();

        // Act
        EnvelopeResult result = await signer.ParseAndVerifyAsync(file, Acme, Ct);

        // Assert
        result.Rejection!.Reason.Should().Be(EnvelopeRejectionReason.TenantMismatch);
        await store.DidNotReceiveWithAnyArgs().GetAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task ParseAndVerifyAsync_KeyIdOfOtherTenant_IsKeyIdTenantMismatchAndStoreNeverCalled()
    {
        // Arrange
        ISecretStore store = Substitute.For<ISecretStore>();
        var signer = new EnvelopeSigner(store);

        // Act
        EnvelopeResult result = await signer.ParseAndVerifyAsync(Job.WithValue("key_id", "globex/1").ToBytes(), Acme, Ct);

        // Assert
        result.Rejection!.Reason.Should().Be(EnvelopeRejectionReason.KeyIdTenantMismatch);
        await store.DidNotReceiveWithAnyArgs().GetAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task ParseAndVerifyAsync_SchemaTwo_IsSchemaUnsupportedAndStoreNeverCalled()
    {
        // Arrange
        ISecretStore store = Substitute.For<ISecretStore>();
        var signer = new EnvelopeSigner(store);

        // Act
        EnvelopeResult result = await signer.ParseAndVerifyAsync(Job.WithValue("schema", "2").ToBytes(), Acme, Ct);

        // Assert
        result.Rejection!.Reason.Should().Be(EnvelopeRejectionReason.SchemaUnsupported);
        EnvelopeWire.ToWire(result.Rejection.Reason).Should().Be("schema_unsupported");
        await store.DidNotReceiveWithAnyArgs().GetAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task ParseAndVerifyAsync_UnknownKeyNumber_IsUnknownKey()
    {
        // Arrange
        var store = await TestKeys.StoreWithBothAsync(Ct);
        await store.SetAsync(Acme, SecretName.Parse("hmac/3"), TestKeys.Secret1, Ct);
        EnvelopeResult signed = await new EnvelopeSigner(store).SignAsync(Parse(Golden.Md("job")), KeyId.Parse("acme/3"), Ct);
        byte[] file = EnvelopeWriter.Serialize(signed.Envelope!);
        var signer = new EnvelopeSigner(await TestKeys.StoreWithBothAsync(Ct));

        // Act
        EnvelopeResult result = await signer.ParseAndVerifyAsync(file, Acme, Ct);

        // Assert
        result.Rejection!.Reason.Should().Be(EnvelopeRejectionReason.UnknownKey);
        result.Rejection.Field.Should().Be("key_id");
    }

    [Fact]
    public async Task ParseAndVerifyAsync_GoldenContextSignedWithKey2_StoreHoldsBoth_IsAccepted()
    {
        // Arrange
        var signer = new EnvelopeSigner(await TestKeys.StoreWithBothAsync(Ct));

        // Act
        EnvelopeResult result = await signer.ParseAndVerifyAsync(Golden.Md("context"), Acme, Ct);

        // Assert
        result.IsAccepted.Should().BeTrue();
    }

    [Fact]
    public async Task ParseAndVerifyAsync_GoldenContextSignedWithKey2_StoreHoldsOnlyKey1_IsUnknownKey()
    {
        // Arrange
        var signer = new EnvelopeSigner(await TestKeys.StoreWithAsync(TestKeys.Acme1, Ct));

        // Act
        EnvelopeResult result = await signer.ParseAndVerifyAsync(Golden.Md("context"), Acme, Ct);

        // Assert
        result.Rejection!.Reason.Should().Be(EnvelopeRejectionReason.UnknownKey);
    }

    [Fact]
    public async Task ParseAndVerifyAsync_SigComputedWithOtherKey_IsInvalidSignature()
    {
        // Arrange
        var signer = new EnvelopeSigner(await TestKeys.StoreWithBothAsync(Ct));
        EnvelopeResult signedWith2 = await signer.SignAsync(Parse(Golden.Md("job")), TestKeys.Acme2, Ct);
        byte[] file = EnvelopeText.From(EnvelopeWriter.Serialize(signedWith2.Envelope!)).WithValue("key_id", "acme/1").ToBytes();

        // Act
        EnvelopeResult result = await signer.ParseAndVerifyAsync(file, Acme, Ct);

        // Assert
        result.Rejection!.Reason.Should().Be(EnvelopeRejectionReason.InvalidSignature);
    }

    [Fact]
    public async Task ParseAndVerifyAsync_SigUppercaseHex_IsAccepted()
    {
        // Arrange
        var signer = new EnvelopeSigner(await TestKeys.StoreWithBothAsync(Ct));
        byte[] file = Job.WithValue("sig", "hmac-sha256:" + Golden.Sig("job").ToUpperInvariant()).ToBytes();

        // Act
        EnvelopeResult result = await signer.ParseAndVerifyAsync(file, Acme, Ct);

        // Assert
        result.IsAccepted.Should().BeTrue();
    }

    [Fact]
    public async Task VerifyAsync_ForeignTenantEnvelope_IsTenantMismatchWithoutLookup()
    {
        // Arrange
        ISecretStore store = Substitute.For<ISecretStore>();
        var signer = new EnvelopeSigner(store);
        TenantId globex = TenantId.Parse("globex");
        Model globexEnvelope = EnvelopeParser.Parse(
            Job.WithValue("tenant", "globex").WithValue("key_id", "globex/1").ToBytes(), globex).Envelope!;

        // Act
        EnvelopeResult result = await signer.VerifyAsync(globexEnvelope, Acme, Ct);

        // Assert
        result.Rejection!.Reason.Should().Be(EnvelopeRejectionReason.TenantMismatch);
        await store.DidNotReceiveWithAnyArgs().GetAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task VerifyAsync_UnsignedEnvelope_IsMissingSignature()
    {
        // Arrange
        var signer = new EnvelopeSigner(await TestKeys.StoreWithBothAsync(Ct));
        Model unsigned = Model.CreateJob(EnvelopeFactoryTests.Header(), new JobFields("calizr"), "x\n"u8.ToArray());

        // Act
        EnvelopeResult result = await signer.VerifyAsync(unsigned, Acme, Ct);

        // Assert
        result.Rejection!.Reason.Should().Be(EnvelopeRejectionReason.MissingSignature);
    }

    [Fact]
    public async Task VerifyAsync_StoreThrows_Propagates()
    {
        // Arrange
        ISecretStore store = Substitute.For<ISecretStore>();
        store.GetAsync(default!, default!, Ct)
            .ReturnsForAnyArgs(Task.FromException<ReadOnlyMemory<byte>?>(new IOException("store unavailable")));
        var signer = new EnvelopeSigner(store);
        Func<Task> act = () => signer.ParseAndVerifyAsync(Golden.Md("job"), Acme, Ct);

        // Act & Assert
        await act.Should().ThrowAsync<IOException>();
    }

    [Fact]
    public async Task Rejections_NeverContainKeyOrDigest()
    {
        // Arrange
        var signer = new EnvelopeSigner(await TestKeys.StoreWithBothAsync(Ct));
        byte[] body = Parse(Golden.Md("job")).Body.ToArray();
        body[0] ^= 0x01;
        EnvelopeResult invalid = await signer.ParseAndVerifyAsync(Job.WithBody(body).ToBytes(), Acme, Ct);
        EnvelopeResult unknown = await signer.ParseAndVerifyAsync(Job.WithValue("key_id", "acme/3").ToBytes(), Acme, Ct);
        string[] forbidden =
        [
            Encoding.UTF8.GetString(TestKeys.Secret1), Encoding.UTF8.GetString(TestKeys.Secret2),
            Convert.ToHexStringLower(TestKeys.Secret1), Convert.ToHexStringLower(TestKeys.Secret2), Golden.Sig("job"),
        ];

        // Act
        string[] details = [invalid.Rejection!.Detail, unknown.Rejection!.Detail];

        // Assert
        invalid.Rejection.Reason.Should().Be(EnvelopeRejectionReason.InvalidSignature);
        unknown.Rejection.Reason.Should().Be(EnvelopeRejectionReason.UnknownKey);
        foreach (string detail in details)
        {
            foreach (string secret in forbidden)
            {
                detail.Should().NotContainEquivalentOf(secret);
            }
        }
    }

    private static Model Parse(byte[] file)
    {
        EnvelopeResult result = EnvelopeParser.Parse(file, Acme);
        result.IsAccepted.Should().BeTrue(result.Rejection?.ToString());
        return result.Envelope!;
    }
}
