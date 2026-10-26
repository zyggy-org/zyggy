using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Envelope;

/// <summary>Spec 09 AC-6 (the Core half): the start-up check tells "absent" and "too short" from "the store cannot be opened", with the owner-facing sentence.</summary>
public sealed class EnvelopeSignerCheckKeyTests
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CheckKeyAsync_Present32Bytes_Usable()
    {
        // Arrange
        InMemorySecretStore store = await TestKeys.StoreWithAsync(TestKeys.Acme1, Ct);

        // Act
        SigningKeyCheck check = await new EnvelopeSigner(store).CheckKeyAsync(TestKeys.Acme1, Ct);

        // Assert
        check.Should().Be(new SigningKeyCheck(SigningKeyState.Usable, string.Empty));
    }

    [Fact]
    public async Task CheckKeyAsync_Absent_MissingDetailNamesStoreAndCommand()
    {
        // Act
        SigningKeyCheck check = await new EnvelopeSigner(new InMemorySecretStore()).CheckKeyAsync(TestKeys.Acme1, Ct);

        // Assert
        check.State.Should().Be(SigningKeyState.Missing);
        check.Detail.Should().Be("signing key acme/1 is not in the memory; run: zyggy secret set hmac/1 --hex");
    }

    [Fact]
    public async Task CheckKeyAsync_Short_TooShortDetailSuggestsGenerate()
    {
        // Arrange
        var store = new InMemorySecretStore();
        await store.SetAsync(Acme, TestKeys.Acme1.SecretName, new byte[16], Ct);

        // Act
        SigningKeyCheck check = await new EnvelopeSigner(store).CheckKeyAsync(TestKeys.Acme1, Ct);

        // Assert
        check.State.Should().Be(SigningKeyState.TooShort);
        check.Detail.Should().Be("signing key acme/1 is unusable (shorter than 32 bytes); run: zyggy secret set hmac/1 --generate");
    }

    [Fact]
    public async Task CheckKeyAsync_StoreThrows_StoreUnavailableDetailCarriesMessage()
    {
        // Act
        SigningKeyCheck check = await new EnvelopeSigner(new ThrowingSecretStore()).CheckKeyAsync(TestKeys.Acme1, Ct);

        // Assert
        check.State.Should().Be(SigningKeyState.StoreUnavailable);
        check.Detail.Should().Be("secret store unavailable: credential-manager: 1312 No such logon session");
    }

    [Fact]
    public async Task CheckKeyAsync_CorruptValue_StoreUnavailableCorruptValue()
    {
        // Arrange: a file store whose value is not hex
        var store = new ThrowingSecretStore { Failure = new InvalidDataException("Secret file 'x' does not contain an even number of hexadecimal characters.") };

        // Act
        SigningKeyCheck check = await new EnvelopeSigner(store).CheckKeyAsync(TestKeys.Acme1, Ct);

        // Assert
        check.State.Should().Be(SigningKeyState.StoreUnavailable);
        check.Detail.Should().Be("secret store unavailable: corrupt value: Secret file 'x' does not contain an even number of hexadecimal characters.");
    }

    [Fact]
    public async Task CheckKeyAsync_NeverContainsKeyBytes()
    {
        // Arrange: every state over the golden key
        var shortStore = new InMemorySecretStore();
        await shortStore.SetAsync(Acme, TestKeys.Acme1.SecretName, TestKeys.Secret1.AsMemory(0, 16), Ct);
        EnvelopeSigner[] signers =
        [
            new(await TestKeys.StoreWithAsync(TestKeys.Acme1, Ct)),
            new(new InMemorySecretStore()),
            new(shortStore),
            new(new ThrowingSecretStore()),
        ];

        // Act
        var details = new List<string>();
        foreach (EnvelopeSigner signer in signers)
        {
            details.Add((await signer.CheckKeyAsync(TestKeys.Acme1, Ct)).Detail);
        }

        // Assert
        SecretSweep.AssertAbsent(details);
    }
}
