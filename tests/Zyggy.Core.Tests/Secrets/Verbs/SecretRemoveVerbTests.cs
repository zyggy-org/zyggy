using Microsoft.Extensions.Logging;

using Zyggy.Core.Secrets;
using Zyggy.Core.Secrets.Verbs;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Secrets.Verbs;

/// <summary>Spec 09 AC-8: <c>zyggy secret remove</c> — the compromise variant's step, never the key this machine signs with.</summary>
public sealed class SecretRemoveVerbTests
{
    private static readonly TenantId Acme = TenantId.Parse("acme");
    private static readonly SecretName Hmac1 = SecretName.Parse("hmac/1");

    private readonly InMemorySecretStore _store = new();
    private readonly RecordingLogger<SecretRemoveVerb> _log = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Run_Present_RemovedLineExitZero()
    {
        // Arrange
        await _store.SetAsync(Acme, Hmac1, TestKeys.Secret1, Ct);
        var console = new VerbConsole();

        // Act
        int exit = await new SecretRemoveVerb(_store, Acme, TestKeys.Acme2, _log).RunAsync(Hmac1, console.Io, Ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        console.Stdout.Should().Be("removed acme/hmac/1\n");
        (await _store.GetAsync(Acme, Hmac1, Ct)).Should().BeNull();
        _log.AtLevel(LogLevel.Information).Should().ContainSingle().Which.Message.Should().Be("removed acme/hmac/1");
    }

    [Fact]
    public async Task Run_Absent_NotInStoreLineExitOne()
    {
        // Arrange
        var console = new VerbConsole();

        // Act
        int exit = await new SecretRemoveVerb(_store, Acme, TestKeys.Acme2, _log).RunAsync(SecretName.Parse("hmac/9"), console.Io, Ct);

        // Assert
        exit.Should().Be(1);
        console.Stderr.Should().Be("zyggy: acme/hmac/9 is not in the memory\n");
    }

    [Fact]
    public async Task Run_SignWithKey_ExitTwoNothingRemoved()
    {
        // Arrange
        await _store.SetAsync(Acme, SecretName.Parse("hmac/2"), TestKeys.Secret2, Ct);
        var console = new VerbConsole();

        // Act
        int exit = await new SecretRemoveVerb(_store, Acme, TestKeys.Acme2, _log).RunAsync(SecretName.Parse("hmac/2"), console.Io, Ct);

        // Assert
        exit.Should().Be(2);
        console.Stderr.Should().Be("zyggy: acme/hmac/2 is this machine's signing key (signWith); change signWith first\n");
        (await _store.GetAsync(Acme, SecretName.Parse("hmac/2"), Ct)).Should().NotBeNull();
    }

    [Fact]
    public async Task Run_StoreUnavailable_ExitSix()
    {
        // Arrange
        var console = new VerbConsole();

        // Act
        int exit = await new SecretRemoveVerb(new ThrowingSecretStore(), Acme, TestKeys.Acme2, _log).RunAsync(Hmac1, console.Io, Ct);

        // Assert
        exit.Should().Be(6);
        console.Stderr.Should().Be("zyggy: secret store unavailable: credential-manager: 1312 No such logon session\n");
    }
}
