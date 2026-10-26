using System.Text;

using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Secrets;
using Zyggy.Core.Secrets.Verbs;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Integration.Cli;

/// <summary>Spec 09 AC-7 in process: the terminal path with a substituted hidden reader (plan 09 A11), and a store that cannot be opened.</summary>
public sealed class SecretCommandInProcessTests : IAsyncLifetime
{
    private CliHarness _harness = null!;

    public async ValueTask InitializeAsync() => _harness = await CliHarness.CreateAsync(CancellationToken.None);

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    [Fact]
    public async Task SecretSet_TerminalStdin_HiddenPromptOnStderrValueStored()
    {
        // Arrange: no stdin → a terminal; the hidden reader answers once; a name the harness did not seed, so no replacement note
        var ct = TestContext.Current.CancellationToken;

        // Act
        var (exit, console) = await _harness.RunAsync(["secret", "set", "api-token"], _harness.CentralNodeJson, stdin: null, more: s => s.AddSingleton<IHiddenInput>(new FakeHiddenInput("abc")), ct: ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        console.Stderr.Should().Be("value for acme/api-token (input is hidden; press Enter when done): ");
        console.Stdout.Should().Be("stored acme/api-token (3 bytes) fingerprint " + SecretFingerprint.Of("abc"u8) + "\n");
        File.ReadAllText(Path.Combine(_harness.SecretsRoot, "acme", "api-token")).Trim().Should().Be(Convert.ToHexStringLower(Encoding.UTF8.GetBytes("abc")));
        SecretSweep.AssertAbsent([console.Stdout, console.Stderr]);
    }

    [Fact]
    public async Task SecretSet_StoreUnavailable_ExitSix()
    {
        // Act
        var (exit, console) = await _harness.RunAsync(["secret", "set", "github-pat"], _harness.CentralNodeJson, stdin: "abc\n", more: s => s.AddSingleton<ISecretStore>(new ThrowingSecretStore()), ct: TestContext.Current.CancellationToken);

        // Assert
        exit.Should().Be(6);
        console.Stderr.Should().Be("zyggy: secret store unavailable: credential-manager: 1312 No such logon session\n");
    }
}
