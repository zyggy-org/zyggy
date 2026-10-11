using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Memory;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Core.Tests.M365;

namespace Zyggy.Core.Tests.Memory;

/// <summary>
/// Spec 38 AC-3, AC-4, AC-21, AC-22, AC-34a: <c>zyggy memory archive offload</c> in process — disabled, unattended, the lock, configuration
/// and usage errors, <c>--dry-run</c>, <c>--probe</c>, <c>--retry</c>, and the run's stdout, stderr and exit codes.
/// </summary>
public sealed class ArchiveVerbOffloadTests : IDisposable
{
    private readonly MemoryTree _tree = new();
    private readonly RecordingProcessRunner _git = new();
    private readonly FakeTimeProvider _clock = new(OffloadFixture.Now);
    private readonly StubOneDrive _drive = new();
    private readonly StubGraphHandler _stub = StubGraphHandler.FromGoldenRoutes();
    private readonly TestCertificates _pair;
    private readonly Dictionary<string, string?> _environment;
    private readonly string _instance;

    public ArchiveVerbOffloadTests()
    {
        _stub.UseDrive(_drive);
        _instance = Path.Combine(_tree.Root, "instance");
        Directory.CreateDirectory(_instance);
        File.Copy(M365Run.Golden("fixtures", "m365.json"), Path.Combine(_instance, "m365.json"));
        _pair = TestCertificates.Create(Path.Combine(_tree.Root, "config"));
        var patterns = Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt");
        _environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["HOME"] = Path.Combine(_tree.Root, "home"),
            ["ZYGGY_STATE_DIR"] = Path.Combine(_tree.Root, "state"),
            ["ZYGGY_MEMORY_ROOT"] = _tree.Root,
            ["ZYGGY_TENANT"] = "acme",
            ["ZYGGY_USER"] = "alice",
            ["ZYGGY_TIMEZONE"] = "Europe/Brussels",
            ["ZYGGY_SECRET_PATTERNS"] = patterns,
            ["ZYGGY_INSTANCE_DIR"] = _instance,
            ["ZYGGY_M365_KEY_FILE"] = _pair.KeyFile,
            ["ZYGGY_M365_CER_FILE"] = _pair.CertificateFile,
        };
        Config("""{"offload": {"enabled": true}}""");
        _tree.Write("archive/zyggy/quote-2026.txt", OffloadFixture.TextItem);
        _tree.Write("archive/zyggy/quote-2026.md", OffloadFixture.Golden("sidecar-text.md"));
        _tree.Write("business/areas/zyggy.md", "---\nname: zyggy\ndescription: zyggy\nupdated: 2026-10-01\n---\n- [stated] 2026-09-30: Archived at archive/zyggy/quote-2026.txt\n");
    }

    private ArchiveStatePaths StatePaths => new(_environment);

    public void Dispose()
    {
        _stub.Violations.Should().BeEmpty();
        _pair.Dispose();
        _tree.Dispose();
    }

    [Fact]
    public async Task Run_Disabled_ExitZeroOneLineNoGraphNoGit()
    {
        // Arrange
        Config("""{"offload": {"enabled": false}}""");

        // Act
        var (exit, console) = await RunAsync();

        // Assert
        exit.Should().Be(0);
        console.Stdout.Should().BeEmpty();
        console.Stderr.Should().Be("offload: disabled (archive.json offload.enabled)\n");
        _stub.Requests.Should().BeEmpty();
        _git.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Run_DisabledHooksOff_SameAsAttended()
    {
        // Arrange
        Config("{}");
        _environment["ZYGGY_HOOKS"] = "off";

        // Act
        var (exit, console) = await RunAsync();

        // Assert
        exit.Should().Be(0);
        console.Stderr.Should().Be("offload: disabled (archive.json offload.enabled)\n");
    }

    [Fact]
    public async Task Run_HooksOff_Enabled_Proceeds()
    {
        // Arrange
        _environment["ZYGGY_HOOKS"] = "off";

        // Act
        var (exit, console) = await RunAsync();

        // Assert
        exit.Should().Be(0, console.Stderr);
        console.Stdout.Should().StartWith("offloaded: archive/zyggy/quote-2026.txt -> Zyggy/archive/zyggy/quote-2026.txt\n");
    }

    [Theory]
    [InlineData("add")]
    [InlineData("remove")]
    public async Task Run_HooksOff_AddRemove_StillRefused(string verb)
    {
        // Arrange
        _environment["ZYGGY_HOOKS"] = "off";
        string[] args = verb == "add" ? ["add", "--project", "zyggy", "--name", "x", "--description", "y", "--file", _pair.KeyFile] : ["remove", "zyggy/quote-2026"];

        // Act
        var console = new VerbConsole();
        var exit = await Verb().RunAsync(args, console.Io, TestContext.Current.CancellationToken);

        // Assert
        exit.Should().Be(ArchiveVerb.Refused);
        console.Stderr.Should().Be("archive: refused: unattended run\n");
    }

    [Fact]
    public async Task Run_LockHeld_ExitZeroAnotherRun()
    {
        // Arrange
        using var held = OffloadLock.TryAcquire(StatePaths.LockFile);

        // Act
        var (exit, console) = await RunAsync();

        // Assert
        exit.Should().Be(0);
        console.Stderr.Should().Be("offload: another run is in progress\n");
        _stub.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("max_failures")]
    [InlineData("m365")]
    [InlineData("patterns")]
    [InlineData("state")]
    public async Task Run_ConfigError_ExitThreeNamesKey(string broken)
    {
        // Arrange
        var expected = broken switch
        {
            "max_failures" => "offload.max_failures",
            "m365" => "m365.json",
            "patterns" => "ZYGGY_SECRET_PATTERNS",
            _ => "offload-state.json",
        };
        switch (broken)
        {
            case "max_failures":
                Config("""{"offload": {"enabled": true, "max_failures": 11}}""");
                break;
            case "m365":
                File.Delete(Path.Combine(_instance, "m365.json"));
                break;
            case "patterns":
                _environment.Remove("ZYGGY_SECRET_PATTERNS");
                _environment.Remove("ZYGGY_INSTANCE_DIR");
                break;
            default:
                Directory.CreateDirectory(StatePaths.StateDirectory);
                File.WriteAllText(StatePaths.StateFile, "{broken");
                break;
        }

        // Act
        var (exit, console) = await RunAsync();

        // Assert
        exit.Should().Be(3);
        console.Stderr.Should().StartWith("offload: configuration error: ").And.Contain(expected).And.EndWith("\n");
        console.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().ContainSingle();
        _stub.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Run_Usage_ExitFourOneLine()
    {
        // Act
        var (exit, console) = await RunAsync("--probe", "--dry-run");

        // Assert
        exit.Should().Be(4);
        console.Stderr.Should().Be("offload: give at most one of --dry-run, --probe, --retry (usage: zyggy memory archive offload [--dry-run | --probe | --retry <project>/<slug>])\n");
    }

    [Fact]
    public async Task Run_DryRun_PrintsCandidatesNoWriteNoGit()
    {
        // Arrange: a dry run works while the offload is still disabled
        Config("{}");

        // Act
        var (exit, console) = await RunAsync("--dry-run");

        // Assert
        exit.Should().Be(0, console.Stderr);
        console.Stdout.Should().Be("would offload: archive/zyggy/quote-2026.txt -> Zyggy/archive/zyggy/quote-2026.txt\n");
        _drive.Posts.Should().Be(0);
        _drive.Puts.Should().Be(0);
        _git.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Run_Probe_PrintsDriveRootOkReadsOnly()
    {
        // Arrange
        _stub.Always("GET", "/items/01ROOTARCH/children\\?", 200, """{"value":[]}""");

        // Act
        var (exit, console) = await RunAsync("--probe");

        // Assert
        exit.Should().Be(0, console.Stderr);
        console.Stdout.Should().Be("drive: b!onedrive0001 root: Zyggy/archive (01ROOTARCH) ok\n");
        _stub.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get || r.Uri.AbsolutePath.EndsWith("/oauth2/v2.0/token", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_Probe_RootMissing_ExitSix()
    {
        // Arrange
        Config("""{"offload": {"enabled": true, "root_folder": "Elsewhere"}}""");

        // Act
        var (exit, console) = await RunAsync("--probe");

        // Assert
        exit.Should().Be(6);
        console.Stderr.Should().Be("offload: root folder Elsewhere missing or not a folder — runbook \"Offload root folder\"\n");
    }

    [Fact]
    public async Task Run_Retry_ParkedItemClearedAndMoved()
    {
        // Arrange
        var state = new OffloadState();
        state.Park("zyggy/quote-2026", OffloadReason.TargetConflict, new DateOnly(2026, 9, 30));
        state.Save(StatePaths);

        // Act
        var (exit, console) = await RunAsync("--retry", "zyggy/quote-2026");

        // Assert
        exit.Should().Be(0, console.Stderr);
        console.Stdout.Should().StartWith("offloaded: archive/zyggy/quote-2026.txt");
        OffloadState.Load(StatePaths).State!.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Run_Retry_NotParked_ExitFour()
    {
        // Act
        var (exit, console) = await RunAsync("--retry", "zyggy/quote-2026");

        // Assert
        exit.Should().Be(4);
        console.Stderr.Should().StartWith("offload: zyggy/quote-2026 is not parked");
        _stub.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Run_OneMovedOneParked_ExitFiveSummaryLine()
    {
        // Arrange: a second indexed item whose copy fails its verification (the folder is 01ID0001, the uploads 01ID0002 and 01ID0003)
        _tree.Write("archive/zyggy/roof-quote.txt", OffloadFixture.TextItem);
        _tree.Write("archive/zyggy/roof-quote.md", OffloadFixture.Golden("sidecar-text.md"));
        _tree.Write("business/areas/zyggy.md", "---\nname: zyggy\ndescription: zyggy\nupdated: 2026-10-01\n---\n- [stated] 2026-09-30: archive/zyggy/quote-2026.txt and archive/zyggy/roof-quote.txt\n");
        _stub.Once("GET", "/items/01ID0003\\?", 200,
            """{"id":"01ID0003","name":"roof-quote.txt","size":68,"file":{"hashes":{"quickXorHash":"AAAAAAAAAAAAAAAAAAAAAAAAAAA="}},"parentReference":{"driveId":"b!onedrive0001","id":"01ID0001"}}""");

        // Act
        var (exit, console) = await RunAsync();

        // Assert
        exit.Should().Be(5, console.Stderr);
        console.Stdout.Should().StartWith("offloaded: archive/zyggy/quote-2026.txt -> Zyggy/archive/zyggy/quote-2026.txt\ncommit: ");
        console.Stderr.Should().Be(
            "offload: zyggy/roof-quote.txt: verify_failed — parked\n"
            + "offload: 1 moved, 0 failed, 1 parked — runbook \"Offload upload failed\"\n");
        _drive.Puts.Should().Be(2);
    }

    [Fact]
    public async Task Run_AllMoved_StdoutLinesExitZero()
    {
        // Act
        var (exit, console) = await RunAsync();

        // Assert
        exit.Should().Be(0, console.Stderr);
        console.Stdout.Should().Be(
            "offloaded: archive/zyggy/quote-2026.txt -> Zyggy/archive/zyggy/quote-2026.txt\ncommit: 0123456789abcdef0123456789abcdef01234567 pushed\n");
        console.Stderr.Should().BeEmpty();
    }

    [Fact]
    public async Task Run_NothingToDo_ExitZeroNoOutput()
    {
        // Arrange: the only item is not indexed
        _tree.Write("business/areas/zyggy.md", "---\nname: zyggy\ndescription: zyggy\nupdated: 2026-10-01\n---\n");

        // Act
        var (exit, console) = await RunAsync();

        // Assert
        exit.Should().Be(0, console.Stderr);
        console.Stdout.Should().BeEmpty();
        console.Stderr.Should().BeEmpty("an unchanged run writes no journal line");
    }

    private void Config(string json) => File.WriteAllText(Path.Combine(_instance, "archive.json"), json);

    private ArchiveVerb Verb() => new(_environment, _clock, id => OffloadFixture.Zone, _git, _stub, checkKeyOwnership: false);

    private async Task<(int Exit, VerbConsole Console)> RunAsync(params string[] args)
    {
        var console = new VerbConsole();
        var exit = await Verb().RunAsync(["offload", .. args], console.Io, TestContext.Current.CancellationToken);
        return (exit, console);
    }
}
