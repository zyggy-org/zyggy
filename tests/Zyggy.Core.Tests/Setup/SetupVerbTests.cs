using System.Text;

using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Configuration;
using Zyggy.Core.Envelope;
using Zyggy.Core.Secrets;
using Zyggy.Core.Setup;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Setup;

/// <summary>Spec 42 Contracts, AC-16, AC-19/AC-20 and plan 42 A2/R5: the verb's order, stop rule and exit priority.</summary>
public sealed class SetupVerbTests : IDisposable
{
    private readonly SetupTestFiles _files = new();
    private readonly RecordingProcessRunner _processes = new();
    private readonly FakeTimeProvider _clock = new(SetupTestFiles.Now);
    private readonly List<NodeConfiguration> _storeRequests = [];

    public SetupVerbTests()
    {
        // A recorded clone leaves what the template would: the two node skills and the secret-pattern file.
        _processes.Hook = spec =>
        {
            if (RecordingProcessRunner.SubCommand(spec) == "clone")
            {
                _files.SeedCoreTemplate();
            }

            return null;
        };
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string PatFingerprint => SecretFingerprint.Of(Encoding.UTF8.GetBytes(TestKeys.TestPat));

    private static string ClonedLine => "setup: zyggy-core: done - cloned " + SetupRequest.DefaultCoreRemote + " at 0123456789ab\n";

    private string ClaudeDir => Path.Combine(_files.UserProfile, ".claude");

    private string SettingsFile => SetupPaths.SettingsFile(ClaudeDir);

    private string Patterns => SetupPaths.SecretPatterns(_files.CorePath);

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
        Directory.Exists(ClaudeDir).Should().BeFalse();
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
        Directory.Exists(ClaudeDir).Should().BeFalse();
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
            + "setup: settings: done - env.ZYGGY_SECRET_PATTERNS set to " + Patterns + " (created " + SettingsFile + ")\n"
            + "setup: settings: done - permissions.deny gained " + string.Join(", ", ClaudeSettingsMerge.DenyRules) + " (created " + SettingsFile + ")\n"
            + "setup: skill remember: done - copied from " + SetupPaths.SkillSource(_files.CorePath, "remember") + "\n"
            + "setup: skill archive: done - copied from " + SetupPaths.SkillSource(_files.CorePath, "archive") + "\n"
            + "setup: skill bus: done - copied from " + SetupPaths.SkillSource(_files.CorePath, "bus") + "\n"
            + "setup: skill discover: done - copied from " + SetupPaths.SkillSource(_files.CorePath, "discover") + "\n"
            + "setup: signing key acme/1: missing - run: zyggy secret set hmac/1 --hex\n"
            + "setup: github-pat: already done - present (fingerprint " + PatFingerprint + ")\n");
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
        InMemorySecretStore store = await AllPresentAsync();
        var console = new VerbConsole();

        // Act
        int exit = await Verb(store).RunAsync(_files.Request(), console.Io, Ct);

        // Assert
        exit.Should().Be(SetupExit.Ok, console.Stdout);
        console.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(10);
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
    public async Task RunAsync_CoreFailedAndAbsent_SkillsFailed_SettingsEnvStillWritten_ExitSix()
    {
        // Arrange
        InMemorySecretStore store = await AllPresentAsync();
        _processes.Hook = null;
        _processes.On("clone", RecordingProcessRunner.Fail(128, "fatal: unable to access 'x': Could not resolve host: example\n"));
        var console = new VerbConsole();

        // Act
        int exit = await Verb(store).RunAsync(_files.Request(), console.Io, Ct);

        // Assert
        exit.Should().Be(SetupExit.Incomplete);
        console.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().Equal(
            "setup: node.json: done - added claude.path (backup " + _files.BackupPath + ")",
            "setup: zyggy-core: failed - fatal: unable to access 'x': Could not resolve host: example",
            "setup: settings: done - env.ZYGGY_SECRET_PATTERNS set to " + Patterns + " (created " + SettingsFile + ")",
            "setup: settings: done - permissions.deny gained " + string.Join(", ", ClaudeSettingsMerge.DenyRules) + " (created " + SettingsFile + ")",
            "setup: skill remember: failed - zyggy-core not available",
            "setup: skill archive: failed - zyggy-core not available",
            "setup: skill bus: failed - zyggy-core not available",
            "setup: skill discover: failed - zyggy-core not available",
            "setup: signing key acme/1: already done - present (fingerprint " + SecretFingerprint.Of(TestKeys.Secret1) + ")",
            "setup: github-pat: already done - present (fingerprint " + PatFingerprint + ")");
        Directory.Exists(Path.Combine(ClaudeDir, "skills")).Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_AllItems_LinesInEnumOrder()
    {
        // Arrange
        InMemorySecretStore store = await AllPresentAsync();
        var console = new VerbConsole();

        // Act
        await Verb(store).RunAsync(_files.Request(), console.Io, Ct);

        // Assert
        console.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split(':')[1].Trim()).Should().Equal(
            "node.json", "zyggy-core", "settings", "settings", "skill remember", "skill archive", "skill bus", "skill discover", "signing key acme/1", "github-pat");
    }

    [Fact]
    public async Task RunAsync_CheckoutWithoutBusAndDiscover_ThoseTwoLeftAsIs_ExitZero()
    {
        // Arrange: spec 15 slice 6 — a zyggy-core that does not hold the two new skills yet (the release window before its own PR merges)
        InMemorySecretStore store = await AllPresentAsync();
        _processes.Hook = spec =>
        {
            if (RecordingProcessRunner.SubCommand(spec) == "clone")
            {
                _files.SeedCoreTemplate(busSkills: false);
            }

            return null;
        };
        var console = new VerbConsole();

        // Act
        int exit = await Verb(store).RunAsync(_files.Request(), console.Io, Ct);

        // Assert
        exit.Should().Be(SetupExit.Ok, console.Stdout);
        console.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => l.StartsWith("setup: skill ", StringComparison.Ordinal)).Should().Equal(
            "setup: skill remember: done - copied from " + SetupPaths.SkillSource(_files.CorePath, "remember"),
            "setup: skill archive: done - copied from " + SetupPaths.SkillSource(_files.CorePath, "archive"),
            "setup: skill bus: left as is - not in this zyggy-core checkout",
            "setup: skill discover: left as is - not in this zyggy-core checkout");
        Directory.GetDirectories(Path.Combine(ClaudeDir, "skills")).Select(Path.GetFileName).Should().BeEquivalentTo("remember", "archive");
    }

    [Fact]
    public async Task RunAsync_BusSkillDiffers_BackedUpThenReplaced_SecondRunAlreadyDone()
    {
        // Arrange: spec 15 AC-15 — the owner's own `bus` skill of that name is moved aside first; a second run changes nothing
        InMemorySecretStore store = await AllPresentAsync();
        string target = SetupPaths.SkillTarget(ClaudeDir, "bus");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "SKILL.md"), "---\nname: bus\n---\nThe owner's own skill.\n");
        string backup = SetupPaths.SkillBackup(_files.LocalAppData, SetupTestFiles.Stamp, "bus");
        var first = new VerbConsole();
        await Verb(store).RunAsync(_files.Request(), first.Io, Ct);
        _processes.Hook = null;
        _processes.On("rev-parse", RecordingProcessRunner.Ok(SetupTestFiles.Slashes(_files.CorePath) + "\n\n"));
        _processes.On("config", RecordingProcessRunner.Ok(SetupRequest.DefaultCoreRemote + "\n"));
        var second = new VerbConsole();

        // Act
        int exit = await Verb(store).RunAsync(_files.Request(), second.Io, Ct);

        // Assert
        exit.Should().Be(SetupExit.Ok, second.Stdout);
        first.Stdout.Should().Contain("setup: skill bus: done - copied from " + SetupPaths.SkillSource(_files.CorePath, "bus") + " (backup " + backup + ")\n")
            .And.Contain("setup: skill discover: done - copied from " + SetupPaths.SkillSource(_files.CorePath, "discover") + "\n");
        File.ReadAllText(Path.Combine(backup, "SKILL.md")).Should().Contain("The owner's own skill.");
        File.ReadAllText(Path.Combine(target, "SKILL.md")).Should().Contain("Submit a job and read its report.");
        second.Stdout.Should().Contain("setup: skill bus: already done\n").And.Contain("setup: skill discover: already done\n");
    }

    [Fact]
    public async Task RunAsync_SecondRun_EveryLineAlreadyDone_NothingRewritten()
    {
        // Arrange
        InMemorySecretStore store = await AllPresentAsync();
        await Verb(store).RunAsync(_files.Request(), new VerbConsole().Io, Ct);
        _processes.Hook = null;
        _processes.On("rev-parse", RecordingProcessRunner.Ok(SetupTestFiles.Slashes(_files.CorePath) + "\n\n"));
        _processes.On("config", RecordingProcessRunner.Ok(SetupRequest.DefaultCoreRemote + "\n"));
        Dictionary<string, DateTime> before = Directory.GetFiles(_files.Root, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.GetLastWriteTimeUtc);
        var console = new VerbConsole();

        // Act
        int exit = await Verb(store).RunAsync(_files.Request(), console.Io, Ct);

        // Assert
        exit.Should().Be(SetupExit.Ok, console.Stdout);
        console.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().OnlyContain(l => l.Contains(": already done", StringComparison.Ordinal));
        Directory.GetFiles(_files.Root, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.GetLastWriteTimeUtc).Should().Equal(before);
    }

    [Theory]
    [InlineData(true, true, "option")]
    [InlineData(false, true, "variable")]
    [InlineData(false, false, "profile")]
    public async Task RunAsync_ClaudeDirOption_WinsOverVariable_WinsOverProfile(bool option, bool variable, string expected)
    {
        // Arrange
        InMemorySecretStore store = await AllPresentAsync();
        var directories = new Dictionary<string, string>
        {
            ["option"] = Path.Combine(_files.Root, "option-claude"),
            ["variable"] = Path.Combine(_files.Root, "variable-claude"),
            ["profile"] = ClaudeDir,
        };
        Dictionary<string, string?> environment = _files.Environment();
        environment[SetupPaths.ClaudeConfigDirVariable] = variable ? directories["variable"] : null;
        string claudeDir = SetupPaths.ResolveClaudeDir(option ? directories["option"] : null, environment);
        var console = new VerbConsole();

        // Act
        await Verb(store, environment).RunAsync(_files.Request() with { ClaudeDir = claudeDir }, console.Io, Ct);

        // Assert
        directories.Where(d => File.Exists(SetupPaths.SettingsFile(d.Value))).Select(d => d.Key).Should().Equal(expected);
        File.Exists(Path.Combine(SetupPaths.SkillTarget(directories[expected], "remember"), "SKILL.md")).Should().BeTrue();
        File.Exists(Path.Combine(SetupPaths.SkillTarget(directories[expected], "archive"), "SKILL.md")).Should().BeTrue();
    }

    private async Task<InMemorySecretStore> AllPresentAsync()
    {
        _files.FakeClaudeExe();
        _files.WriteNodeJson(_files.SpikeJson(signWith: "acme/1"));
        InMemorySecretStore store = await TestKeys.StoreWithAsync(TestKeys.Acme1, Ct);
        await store.SetAsync(SetupTestFiles.Acme, TestKeys.GitHubPat, Encoding.UTF8.GetBytes(TestKeys.TestPat), Ct);
        return store;
    }

    private SetupVerb Verb(ISecretStore store, Dictionary<string, string?>? environment = null) =>
        new(_processes, _clock, environment ?? _files.Environment(), new Dictionary<string, string?>(), configuration =>
        {
            _storeRequests.Add(configuration);
            return store;
        });
}
