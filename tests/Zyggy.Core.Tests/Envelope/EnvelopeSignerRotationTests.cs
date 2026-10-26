using Zyggy.Core.Envelope;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Envelope;

/// <summary>
/// Spec 09 AC-5, the pin <c>rotate-hmac.md</c> relies on (founding spec §4 as amended by A9-1): after a rotation the new key signs,
/// the old key stays verify-only while present, and its removal turns its files into <c>unknown_key</c> naming it. No signer change.
/// </summary>
public sealed class EnvelopeSignerRotationTests
{
    private static readonly TenantId Acme = TenantId.Parse("acme");
    private static readonly MachineName Central = MachineName.Parse("central");
    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SignAsync_SignWithAcme2OverBothKeys_NewJobCarriesKeyIdAcme2()
    {
        // Arrange: both keys in the store, signWith switched to acme/2
        InMemorySecretStore store = await TestKeys.StoreWithBothAsync(Ct);

        // Act
        Model job = await TestEnvelopes.SignedJobAsync(Central, HomeLaptop, store, TestKeys.Acme2, "probe\n", Ct);

        // Assert
        job.KeyId.Should().Be(TestKeys.Acme2);
        (await new EnvelopeSigner(store).VerifyAsync(job, Acme, Ct)).IsAccepted.Should().BeTrue();
    }

    [Fact]
    public async Task VerifyAsync_FileSignedWithAcme1BeforeRotation_AcceptedWhileKeyPresent()
    {
        // Arrange: a file signed before the rotation
        InMemorySecretStore store = await TestKeys.StoreWithBothAsync(Ct);
        byte[] older = await TestEnvelopes.SignedJobBytesAsync(Central, HomeLaptop, store, TestKeys.Acme1, "probe\n", Ct);

        // Act: verified over the store that now signs with acme/2 but still holds acme/1
        EnvelopeResult result = await new EnvelopeSigner(store).ParseAndVerifyAsync(older, Acme, Ct);

        // Assert: verify-only by construction — nothing marks the key as retired
        result.IsAccepted.Should().BeTrue(result.Rejection?.ToString());
        result.Envelope!.KeyId.Should().Be(TestKeys.Acme1);
    }

    [Fact]
    public async Task VerifyAsync_AfterRemoveAsyncHmac1_UnknownKeyNamingAcme1()
    {
        // Arrange: the compromise variant — the retired key removed
        InMemorySecretStore store = await TestKeys.StoreWithBothAsync(Ct);
        byte[] older = await TestEnvelopes.SignedJobBytesAsync(Central, HomeLaptop, store, TestKeys.Acme1, "probe\n", Ct);
        await store.RemoveAsync(Acme, TestKeys.Acme1.SecretName, Ct);

        // Act
        EnvelopeResult result = await new EnvelopeSigner(store).ParseAndVerifyAsync(older, Acme, Ct);

        // Assert: the drift 08's verify prints as `unknown_key (acme/1)`
        result.IsAccepted.Should().BeFalse();
        result.Rejection!.Reason.Should().Be(EnvelopeRejectionReason.UnknownKey);
        result.Rejection.Field.Should().Be("key_id");
        EnvelopeParser.Parse(older, Acme).Envelope!.KeyId.Should().Be(TestKeys.Acme1);
    }
}
