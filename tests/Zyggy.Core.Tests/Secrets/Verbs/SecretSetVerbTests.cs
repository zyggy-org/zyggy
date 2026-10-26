using System.Text;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Zyggy.Core.Secrets;
using Zyggy.Core.Secrets.Verbs;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Secrets.Verbs;

/// <summary>Spec 09 AC-7: <c>zyggy secret set</c> as a Core verb — every way a value arrives, every refusal, and never the value by accident.</summary>
public sealed partial class SecretSetVerbTests : IDisposable
{
    private static readonly TenantId Acme = TenantId.Parse("acme");
    private static readonly SecretName Hmac1 = SecretName.Parse("hmac/1");
    private static readonly SecretName Hmac2 = SecretName.Parse("hmac/2");
    private static readonly SecretName GitHubPat = SecretName.Parse("github-pat");

    private readonly InMemorySecretStore _store = new();
    private readonly RecordingLogger<SecretSetVerb> _log = new();
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "zyggy-ut", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    [Fact]
    public async Task Run_RedirectedRaw_StoresBytesWithoutTrailingNewline()
    {
        // Arrange
        var console = new VerbConsole("abc\n");

        // Act
        int exit = await Verb().RunAsync(new SecretSetRequest(GitHubPat, Hex: false, Generate: false, Show: false), console.Io, StdinKind.Redirected, new FakeHiddenInput(null), Ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        (await _store.GetAsync(Acme, GitHubPat, Ct))!.Value.ToArray().Should().Equal(Encoding.UTF8.GetBytes("abc"));
        console.Stdout.Should().Be("stored acme/github-pat (3 bytes) fingerprint " + SecretFingerprint.Of("abc"u8) + "\n");
        AssertValueNowhere(console, "abc");
    }

    [Fact]
    public async Task Run_RedirectedHexCrlf_StoresTwoBytes()
    {
        // Arrange
        var console = new VerbConsole("0a0b\r\n");

        // Act
        int exit = await Verb().RunAsync(new SecretSetRequest(Hmac1, Hex: true, Generate: false, Show: false), console.Io, StdinKind.Redirected, new FakeHiddenInput(null), Ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        (await _store.GetAsync(Acme, Hmac1, Ct))!.Value.ToArray().Should().Equal(0x0a, 0x0b);
        console.Stdout.Should().StartWith("stored acme/hmac/1 (2 bytes) fingerprint ");
    }

    [Fact]
    public async Task Run_Terminal_HiddenReaderUsedPromptOnStderrValueNowhere()
    {
        // Arrange
        var console = new VerbConsole();
        var hidden = new FakeHiddenInput("abc");

        // Act
        int exit = await Verb().RunAsync(new SecretSetRequest(GitHubPat, Hex: false, Generate: false, Show: false), console.Io, StdinKind.Terminal, hidden, Ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        hidden.Called.Should().BeTrue();
        console.Stderr.Should().Be("value for acme/github-pat (input is hidden; press Enter when done): ");
        console.Stdout.Should().Be("stored acme/github-pat (3 bytes) fingerprint " + SecretFingerprint.Of("abc"u8) + "\n");
        AssertValueNowhere(console, "abc");
    }

    [Fact]
    public async Task Run_Generate_32RandomBytesNothingReadFingerprintLine()
    {
        // Arrange
        var console = new VerbConsole();
        var hidden = new FakeHiddenInput("never");

        // Act
        int exit = await Verb().RunAsync(new SecretSetRequest(Hmac2, Hex: false, Generate: true, Show: false), console.Io, StdinKind.Terminal, hidden, Ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        hidden.Called.Should().BeFalse();
        byte[] stored = (await _store.GetAsync(Acme, Hmac2, Ct))!.Value.ToArray();
        stored.Should().HaveCount(32);
        console.Stdout.Should().Be($"stored acme/hmac/2 (32 bytes) fingerprint {SecretFingerprint.Of(stored)}\n");
        StoredLine().IsMatch(console.Stdout).Should().BeTrue(console.Stdout);
        AssertValueNowhere(console, Convert.ToHexStringLower(stored));
    }

    [Fact]
    public async Task Run_GenerateShow_SecondLineIsExactlyTheHexOfTheStoredBytes()
    {
        // Arrange
        var console = new VerbConsole();

        // Act
        int exit = await Verb().RunAsync(new SecretSetRequest(Hmac2, Hex: false, Generate: true, Show: true), console.Io, StdinKind.Redirected, new FakeHiddenInput(null), Ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        byte[] stored = (await _store.GetAsync(Acme, Hmac2, Ct))!.Value.ToArray();
        string[] lines = console.Stdout.TrimEnd('\n').Split('\n');
        lines.Should().HaveCount(2);
        lines[1].Should().Be("value (hex, shown once; carry it to the other machines of the tenant): " + Convert.ToHexStringLower(stored));
        console.Stderr.Should().BeEmpty();
        _log.Messages.Should().NotContain(m => m.Contains(Convert.ToHexStringLower(stored), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_GenerateWithStdinInput_ExitTwoNothingStored()
    {
        // Arrange: plan 09 A9 — a value piped in together with --generate is a mistake, not an input
        var console = new VerbConsole("deadbeef\n");

        // Act
        int exit = await Verb().RunAsync(new SecretSetRequest(Hmac2, Hex: false, Generate: true, Show: false), console.Io, StdinKind.Redirected, new FakeHiddenInput(null), Ct);

        // Assert
        exit.Should().Be(2);
        console.Stderr.Should().Be("zyggy: --generate takes no input\n");
        (await _store.ListAsync(Acme, Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Run_GenerateNonHmacName_ExitTwo()
    {
        // Arrange
        var console = new VerbConsole();

        // Act
        int exit = await Verb().RunAsync(new SecretSetRequest(GitHubPat, Hex: false, Generate: true, Show: false), console.Io, StdinKind.Terminal, new FakeHiddenInput(null), Ct);

        // Assert
        exit.Should().Be(2);
        console.Stderr.Should().Be("zyggy: --generate is for hmac/<n> keys\n");
    }

    [Fact]
    public async Task Run_ShowWithoutGenerate_ExitTwo()
    {
        // Arrange
        var console = new VerbConsole("abc\n");

        // Act
        int exit = await Verb().RunAsync(new SecretSetRequest(GitHubPat, Hex: false, Generate: false, Show: true), console.Io, StdinKind.Redirected, new FakeHiddenInput(null), Ct);

        // Assert
        exit.Should().Be(2);
        console.Stderr.Should().Be("zyggy: --show needs --generate\n");
        (await _store.ListAsync(Acme, Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Run_Empty_ExitTwo()
    {
        // Arrange
        var console = new VerbConsole("\n");

        // Act
        int exit = await Verb().RunAsync(new SecretSetRequest(GitHubPat, Hex: false, Generate: false, Show: false), console.Io, StdinKind.Redirected, new FakeHiddenInput(null), Ct);

        // Assert
        exit.Should().Be(2);
        console.Stderr.Should().Be("zyggy: no value on standard input\n");
    }

    [Fact]
    public async Task Run_NotHex_ExitTwo()
    {
        // Arrange
        var console = new VerbConsole("xyz\n");

        // Act
        int exit = await Verb().RunAsync(new SecretSetRequest(Hmac1, Hex: true, Generate: false, Show: false), console.Io, StdinKind.Redirected, new FakeHiddenInput(null), Ct);

        // Assert
        exit.Should().Be(2);
        console.Stderr.Should().Be("zyggy: value is not hexadecimal\n");
    }

    [Fact]
    public async Task Run_513Bytes_ExitTwoNothingStored()
    {
        // Arrange
        var console = new VerbConsole(new string('a', 513) + "\n");

        // Act
        int exit = await Verb().RunAsync(new SecretSetRequest(GitHubPat, Hex: false, Generate: false, Show: false), console.Io, StdinKind.Redirected, new FakeHiddenInput(null), Ct);

        // Assert
        exit.Should().Be(2);
        console.Stderr.Should().Be("zyggy: value is larger than 512 bytes\n");
        (await _store.ListAsync(Acme, Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Run_512Bytes_Stored()
    {
        // Arrange
        var console = new VerbConsole(new string('a', 512));

        // Act
        int exit = await Verb().RunAsync(new SecretSetRequest(GitHubPat, Hex: false, Generate: false, Show: false), console.Io, StdinKind.Redirected, new FakeHiddenInput(null), Ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        console.Stdout.Should().StartWith("stored acme/github-pat (512 bytes) fingerprint ");
    }

    [Fact]
    public async Task Run_Replace_NoteWithPreviousFingerprintOnStderr()
    {
        // Arrange
        await _store.SetAsync(Acme, Hmac1, TestKeys.Secret1, Ct);
        var console = new VerbConsole(Convert.ToHexStringLower(TestKeys.Secret2) + "\n");

        // Act
        int exit = await Verb().RunAsync(new SecretSetRequest(Hmac1, Hex: true, Generate: false, Show: false), console.Io, StdinKind.Redirected, new FakeHiddenInput(null), Ct);

        // Assert
        exit.Should().Be(0);
        console.Stderr.Should().Be($"note: acme/hmac/1 replaced (previous fingerprint {SecretFingerprint.Of(TestKeys.Secret1)})\n");
        (await _store.GetAsync(Acme, Hmac1, Ct))!.Value.ToArray().Should().Equal(TestKeys.Secret2);
        AssertValueNowhere(console, Convert.ToHexStringLower(TestKeys.Secret2));
    }

    [Fact]
    public async Task Run_TerminalAborted_ExitFiveNothingStored()
    {
        // Arrange
        var console = new VerbConsole();

        // Act
        int exit = await Verb().RunAsync(new SecretSetRequest(GitHubPat, Hex: false, Generate: false, Show: false), console.Io, StdinKind.Terminal, new FakeHiddenInput(null), Ct);

        // Assert
        exit.Should().Be(5);
        console.Stderr.Should().EndWith("zyggy: aborted; nothing stored\n");
        (await _store.ListAsync(Acme, Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Run_StoreUnavailable_ExitSixOneLine()
    {
        // Arrange
        var console = new VerbConsole("abc\n");
        var verb = new SecretSetVerb(new ThrowingSecretStore(), Acme, _log);

        // Act
        int exit = await verb.RunAsync(new SecretSetRequest(GitHubPat, Hex: false, Generate: false, Show: false), console.Io, StdinKind.Redirected, new FakeHiddenInput(null), Ct);

        // Assert
        exit.Should().Be(6);
        console.Stderr.Should().Be("zyggy: secret store unavailable: credential-manager: 1312 No such logon session\n");
    }

    [Fact]
    public async Task Run_CredentialsDirectoryStillProvides_NoteOnStderr()
    {
        // Arrange: Central — the unit's credentials directory already provides hmac/1; the owner writes the file layer
        string credentials = Path.Combine(_scratch, "credentials");
        Directory.CreateDirectory(credentials);
        await File.WriteAllBytesAsync(Path.Combine(credentials, "zyggy.acme.hmac.1"), TestKeys.Secret1, Ct);
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal) { ["CREDENTIALS_DIRECTORY"] = credentials };
        var files = new FileSecretStore(Options.Create(new FileSecretStoreOptions { RootDirectory = Path.Combine(_scratch, "secrets") }));
        var linux = new LinuxSecretStore(environment, files, new RecordingLogger<LinuxSecretStore>(), checkOwnership: false);
        var console = new VerbConsole(Convert.ToHexStringLower(TestKeys.Secret2) + "\n");

        // Act
        int exit = await new SecretSetVerb(linux, Acme, _log).RunAsync(new SecretSetRequest(Hmac1, Hex: true, Generate: false, Show: false), console.Io, StdinKind.Redirected, new FakeHiddenInput(null), Ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        console.Stderr.Should().Contain("note: the credentials directory still provides hmac/1 to the node\n");
        (await files.GetAsync(Acme, Hmac1, Ct))!.Value.ToArray().Should().Equal(TestKeys.Secret2);
    }

    [Fact]
    public async Task Run_NeverLogsOrPrintsTheValue()
    {
        // Arrange: the golden key through every input path
        string hex = Convert.ToHexStringLower(TestKeys.Secret1);
        var redirected = new VerbConsole(hex + "\n");
        var terminal = new VerbConsole();

        // Act
        await Verb().RunAsync(new SecretSetRequest(Hmac1, Hex: true, Generate: false, Show: false), redirected.Io, StdinKind.Redirected, new FakeHiddenInput(null), Ct);
        await Verb().RunAsync(new SecretSetRequest(Hmac2, Hex: true, Generate: false, Show: false), terminal.Io, StdinKind.Terminal, new FakeHiddenInput(hex), Ct);

        // Assert
        SecretSweep.AssertAbsent([redirected.Stdout, redirected.Stderr, terminal.Stdout, terminal.Stderr, .. _log.Messages]);
        _log.AtLevel(LogLevel.Information).Should().HaveCount(2).And.AllSatisfy(e => e.Message.Should().StartWith("stored acme/hmac/"));
    }

    private SecretSetVerb Verb() => new(_store, Acme, _log);

    private void AssertValueNowhere(VerbConsole console, string value)
    {
        console.Stdout.Should().NotContain(value);
        console.Stderr.Should().NotContain(value);
        _log.Messages.Should().NotContain(m => m.Contains(value, StringComparison.Ordinal));
    }

    [GeneratedRegex(@"^stored acme/hmac/2 \(32 bytes\) fingerprint [0-9a-f]{8}\n$")]
    private static partial Regex StoredLine();
}
