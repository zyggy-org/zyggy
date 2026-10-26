using System.Text;

using Zyggy.Core.Secrets;
using Zyggy.Core.Secrets.Verbs;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Secrets.Verbs;

/// <summary>Spec 09 AC-8: <c>zyggy secret list</c> — names, sizes, fingerprints, the store and the key's role; never a value.</summary>
public sealed class SecretListVerbTests
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private readonly InMemorySecretStore _store = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Run_ThreeNames_TableOrdinalWithRoles()
    {
        // Arrange: signWith acme/2 — hmac/1 is the retired, verify-only key
        await _store.SetAsync(Acme, SecretName.Parse("hmac/1"), TestKeys.Secret1, Ct);
        await _store.SetAsync(Acme, SecretName.Parse("hmac/2"), TestKeys.Secret2, Ct);
        await _store.SetAsync(Acme, SecretName.Parse("github-pat"), Encoding.UTF8.GetBytes(TestKeys.TestPat), Ct);
        var console = new VerbConsole();

        // Act
        int exit = await new SecretListVerb(_store, Acme, TestKeys.Acme2).RunAsync(console.Io, Ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        string pat = SecretFingerprint.Of(Encoding.UTF8.GetBytes(TestKeys.TestPat));
        string one = SecretFingerprint.Of(TestKeys.Secret1);
        string two = SecretFingerprint.Of(TestKeys.Secret2);
        console.Stdout.Should().Be(
            "name        bytes  fingerprint  store   role\n"
            + $"github-pat  19     {pat}     memory  \n"
            + $"hmac/1      32     {one}     memory  verify-only\n"
            + $"hmac/2      32     {two}     memory  signs\n");
    }

    [Fact]
    public async Task Run_Empty_PrintsNone()
    {
        // Arrange
        var console = new VerbConsole();

        // Act
        int exit = await new SecretListVerb(_store, Acme, TestKeys.Acme1).RunAsync(console.Io, Ct);

        // Assert
        exit.Should().Be(0);
        console.Stdout.Should().Be("(none)\n");
    }

    [Fact]
    public async Task Run_NoSignWith_NoSignsRow()
    {
        // Arrange
        await _store.SetAsync(Acme, SecretName.Parse("hmac/1"), TestKeys.Secret1, Ct);
        var console = new VerbConsole();

        // Act
        int exit = await new SecretListVerb(_store, Acme, signWith: null).RunAsync(console.Io, Ct);

        // Assert
        exit.Should().Be(0);
        console.Stdout.Should().NotContain("signs").And.Contain("verify-only");
    }

    [Fact]
    public async Task Run_NeverPrintsAValue()
    {
        // Arrange
        await _store.SetAsync(Acme, SecretName.Parse("hmac/1"), TestKeys.Secret1, Ct);
        await _store.SetAsync(Acme, SecretName.Parse("github-pat"), Encoding.UTF8.GetBytes(TestKeys.TestPat), Ct);
        var console = new VerbConsole();

        // Act
        await new SecretListVerb(_store, Acme, TestKeys.Acme1).RunAsync(console.Io, Ct);

        // Assert
        SecretSweep.AssertAbsent([console.Stdout, console.Stderr]);
        console.Stdout.Should().NotContain(Convert.ToHexStringLower(TestKeys.Secret1));
    }

    [Fact]
    public async Task Run_StoreUnavailable_ExitSix()
    {
        // Arrange
        var console = new VerbConsole();

        // Act
        int exit = await new SecretListVerb(new ThrowingSecretStore(), Acme, TestKeys.Acme1).RunAsync(console.Io, Ct);

        // Assert
        exit.Should().Be(6);
        console.Stderr.Should().Be("zyggy: secret store unavailable: credential-manager: 1312 No such logon session\n");
    }
}
