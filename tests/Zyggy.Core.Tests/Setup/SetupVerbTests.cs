using System.Text;

using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Configuration;
using Zyggy.Core.Envelope;
using Zyggy.Core.Secrets;
using Zyggy.Core.Setup;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Setup;

/// <summary>Spec 42 Contracts, AC-19/AC-20 and plan 42 A2: the verb's order, stop rule and exit priority.</summary>
public sealed class SetupVerbTests : IDisposable
{
    private readonly SetupTestFiles _files = new();
    private readonly RecordingProcessRunner _processes = new();
    private readonly FakeTimeProvider _clock = new(SetupTestFiles.Now);
    private readonly List<NodeConfiguration> _storeRequests = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _files.Dispose();

    [Fact]
    public async Task RunAsync_MachineCentral_ExitTwoOneLine_NothingReadOrWritten()
    {
        // Arrange
        string config = _files.WriteNodeJson(_files.SpikeJson(machine: "central"));
        byte[] original = File.ReadAllBytes(config);
        var console = new VerbConsole();

        // Act
        int exit = await Verb(new InMemorySecretStore()).RunAsync(_files.Request(TestKeys.Acme1, machine: "central"), console.Io, Ct);

        // Assert
        exit.Should().Be(SetupExit.Usage);
        console.Stdout.Should().Be("setup: node.json: failed - setup is for laptops; Central is installed by install-node.md\n");
        File.ReadAllBytes(config).Should().Equal(original);
        Directory.GetFiles(Path.GetDirectoryName(config)!).Should().Equal(config);
        _storeRequests.Should().BeEmpty();
        _processes.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_NodeJsonRefused_ExitThree_StopsBeforeSecrets_StoreNeverAsked()
    {
        // Arrange
        _files.FakeClaudeExe();
        _files.WriteNodeJson(_files.SpikeJson());
        var console = new VerbConsole();

        // Act
        int exit = await Verb(new InMemorySecretStore()).RunAsync(_files.Request(), console.Io, Ct);

        // Assert
        exit.Should().Be(SetupExit.NodeConfig);
        console.Stdout.Should().Be("setup: node.json: failed - signWith missing; run install.ps1 again with -SignWith acme/<n>\n");
        _storeRequests.Should().BeEmpty();
        _processes.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_CompleteFileSigningKeyMissing_ExitFour_LinesInItemOrder()
    {
        // Arrange
        string exe = _files.FakeClaudeExe();
        _files.WriteNodeJson(_files.SpikeJson(signWith: "acme/1", extra: $"\"claude\": {{ \"path\": \"{SetupTestFiles.Slashes(exe)}\" }}"));
        InMemorySecretStore store = await TestKeys.StoreWithPatAsync(SetupTestFiles.Acme, Ct);
        var console = new VerbConsole();

        // Act
        int exit = await Verb(store).RunAsync(_files.Request(), console.Io, Ct);

        // Assert
        exit.Should().Be(SetupExit.SigningKeyMissing);
        console.Stdout.Should().Be(
            "setup: node.json: already done\n"
            + ClonedLine
            + "setup: signing key acme/1: missing - run: zyggy secret set hmac/1 --hex\n"
            + "setup: github-pat: already done - present (fingerprint " + SecretFingerprint.Of(Encoding.UTF8.GetBytes(TestKeys.TestPat)) + ")\n");
    }

    [Fact]
    public async Task RunAsync_OnlyPatMissing_ExitSix()
    {
        // Arrange
        _files.FakeClaudeExe();
        _files.WriteNodeJson(_files.SpikeJson(signWith: "acme/1"));
        InMemorySecretStore store = await TestKeys.StoreWithAsync(TestKeys.Acme1, Ct);
        var console = new VerbConsole();

        // Act
        int exit = await Verb(store).RunAsync(_files.Request(), console.Io, Ct);

        // Assert
        exit.Should().Be(SetupExit.Incomplete);
    }

    [Fact]
    public async Task RunAsync_AllPresent_ExitZero_GitCalledOnlyForZyggyCore()
    {
        // Arrange
        _files.FakeClaudeExe();
        _files.WriteNodeJson(_files.SpikeJson(signWith: "acme/1"));
        InMemorySecretStore store = await TestKeys.StoreWithAsync(TestKeys.Acme1, Ct);
        await store.SetAsync(SetupTestFiles.Acme, TestKeys.GitHubPat, Encoding.UTF8.GetBytes(TestKeys.TestPat), Ct);
        var console = new VerbConsole();

        // Act
        int exit = await Verb(store).RunAsync(_files.Request(), console.Io, Ct);

        // Assert
        exit.Should().Be(SetupExit.Ok, console.Stdout);
        console.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(4);
        _processes.Calls.Select(RecordingProcessRunner.SubCommand).Should().Equal("clone", "rev-parse");
        _processes.Calls.All(c => c.FileName == "git").Should().BeTrue();
        _storeRequests.Should().ContainSingle().Which.Claude.Path.Should().NotBeNull();
    }

    [Fact]
    public async Task RunAsync_SigningKeyNamedIsTheCompletedFilesSignWith()
    {
        // Arrange
        _files.FakeClaudeExe();
        _files.WriteNodeJson(_files.SpikeJson());
        InMemorySecretStore store = await TestKeys.StoreWithAsync(TestKeys.Acme2, Ct);
        var console = new VerbConsole();

        // Act
        await Verb(store).RunAsync(_files.Request(KeyId.Parse("acme/2")), console.Io, Ct);

        // Assert
        console.Stdout.Should().Contain("setup: signing key acme/2: already done - present (fingerprint " + SecretFingerprint.Of(TestKeys.Secret2) + ")\n");
    }

    [Fact]
    public async Task RunAsync_CoreFailed_ExitSix_SecretsStillReported()
    {
        // Arrange
        _files.FakeClaudeExe();
        _files.WriteNodeJson(_files.SpikeJson(signWith: "acme/1"));
        InMemorySecretStore store = await TestKeys.StoreWithAsync(TestKeys.Acme1, Ct);
        await store.SetAsync(SetupTestFiles.Acme, TestKeys.GitHubPat, Encoding.UTF8.GetBytes(TestKeys.TestPat), Ct);
        _processes.On("clone", RecordingProcessRunner.Fail(128, "fatal: unable to access 'x': Could not resolve host: example\n"));
        var console = new VerbConsole();

        // Act
        int exit = await Verb(store).RunAsync(_files.Request(), console.Io, Ct);

        // Assert
        exit.Should().Be(SetupExit.Incomplete);
        console.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().Equal(
            "setup: node.json: done - added claude.path (backup " + _files.BackupPath + ")",
            "setup: zyggy-core: failed - fatal: unable to access 'x': Could not resolve host: example",
            "setup: signing key acme/1: already done - present (fingerprint " + SecretFingerprint.Of(TestKeys.Secret1) + ")",
            "setup: github-pat: already done - present (fingerprint " + SecretFingerprint.Of(Encoding.UTF8.GetBytes(TestKeys.TestPat)) + ")");
    }

    [Fact]
    public async Task RunAsync_LinesInItemOrder_NodeJsonThenCoreThenSecrets()
    {
        // Arrange
        _files.FakeClaudeExe();
        _files.WriteNodeJson(_files.SpikeJson(signWith: "acme/1"));
        InMemorySecretStore store = await TestKeys.StoreWithAsync(TestKeys.Acme1, Ct);
        var console = new VerbConsole();

        // Act
        await Verb(store).RunAsync(_files.Request(), console.Io, Ct);

        // Assert
        console.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split(':')[1].Trim()).Should().Equal(
            "node.json", "zyggy-core", "signing key acme/1", "github-pat");
    }

    private static string ClonedLine => "setup: zyggy-core: done - cloned " + SetupRequest.DefaultCoreRemote + " at 0123456789ab\n";

    private SetupVerb Verb(ISecretStore store) =>
        new(_processes, _clock, _files.Environment(), new Dictionary<string, string?>(), configuration =>
        {
            _storeRequests.Add(configuration);
            return store;
        });
}
