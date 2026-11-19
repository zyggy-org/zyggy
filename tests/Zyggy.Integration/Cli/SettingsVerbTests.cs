using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Configuration;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Cli;

/// <summary>
/// Spec 55 slice 4 through the real <c>zyggy</c> entry point (in process), a real <c>node.json</c>, the real discovery scanner and the
/// bus checkout of the harness: an unattended run changes nothing but still shows (AC-7), a laptop's file is never changed (AC-8), and
/// on Central the owner's root with its exclude lands in one write, then the supervision project, each with before, after and what the
/// root publishes; a missing folder is refused (AC-9).
/// </summary>
public sealed class SettingsVerbTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T = new(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);

    private static readonly Dictionary<string, string?> Unattended = new() { ["ZYGGY_HOOKS"] = "off" };

    private static readonly string[] HomeOnly = ["home"];

    private readonly FakeTimeProvider _clock = new(T);

    private CliHarness _harness = null!;

    private string _parent = null!;

    public async ValueTask InitializeAsync()
    {
        _harness = await CliHarness.CreateAsync(CancellationToken.None);
        _parent = Directory.CreateDirectory(_harness.Scratch.File("srv")).FullName;
        Directory.CreateDirectory(Path.Combine(_parent, "instance", ".claude"));
        Directory.CreateDirectory(Path.Combine(_parent, "home", ".claude"));
    }

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    /// <summary>The three changing forms, each with a value that would pass its check on a Central-shaped file.</summary>
    public static TheoryData<string> ChangingForms() => new()
    {
        "set supervision.project instance",
        "add discovery.exclude build-*",
        "remove discovery.exclude home",
    };

    // AC-7
    [Theory]
    [MemberData(nameof(ChangingForms))]
    public async Task Change_Unattended_ExitFive_FileUnchanged(string form)
    {
        // Arrange
        string config = CentralShaped(supervisionProject: null);
        string before = await TextOfAsync(config);

        // Act
        var (exit, console) = await RunAsync(["settings", .. form.Split(' ')], config, Unattended);

        // Assert
        (exit, console.Stderr, await TextOfAsync(config))
            .Should().Be((5, "zyggy: refused: settings are changed only in an attended session\n", before));
    }

    // AC-7
    [Fact]
    public async Task Show_Unattended_PrintsTheFiveKeys_ExitZero()
    {
        // Arrange
        string config = CentralShaped(supervisionProject: "instance");
        string root = _parent.Replace('\\', '/');

        // Act
        var (exit, console) = await RunAsync(["settings", "show"], config, Unattended);

        // Assert
        (exit, console.Stdout).Should().Be((0,
            $"devRoots: {root}\n" +
            "discovery.exclude: home\n" +
            "supervision.project: instance\n" +
            "supervision.workMachines: (none)\n" +
            "supervision.outboundPaths: (none)\n"));
    }

    // AC-7
    [Fact]
    public async Task Show_FileTheReaderRefuses_ExitThree()
    {
        // Arrange
        string config = _harness.Scratch.File("broken-node.json");
        await File.WriteAllTextAsync(config, "{ \"tenant\": \"acme\", ", TestContext.Current.CancellationToken);

        // Act
        var (exit, console) = await RunAsync(["settings", "show"], config, Unattended);

        // Assert
        (exit, console.Stdout, console.Stderr.StartsWith("zyggy: ", StringComparison.Ordinal)).Should().Be((3, string.Empty, true));
    }

    // AC-8
    [Theory]
    [MemberData(nameof(ChangingForms))]
    public async Task Change_OnALaptop_ExitThree_FileUnchanged(string form)
    {
        // Arrange
        string config = _harness.WriteNodeJson(
            CliHarness.HomeLaptop,
            _harness.Bus.CloneDir,
            "laptop-shaped",
            devRoots: [_parent],
            discoveryJson: "{ \"exclude\": [\"home\"] }");
        string before = await TextOfAsync(config);

        // Act
        var (exit, console) = await RunAsync(["settings", .. form.Split(' ')], config);

        // Assert
        (exit, console.Stderr, await TextOfAsync(config))
            .Should().Be((3, "zyggy: settings are changed by this verb only on Central\n", before));
    }

    // AC-9
    [Fact]
    public async Task AddDevRootsWithExclude_ThenSetProject_OneWriteEach_PublishesInstanceNotHome()
    {
        // Arrange
        string config = _harness.CentralNodeJson;
        byte[] original = await File.ReadAllBytesAsync(config, TestContext.Current.CancellationToken);

        // Act
        var (addExit, add) = await RunAsync(["settings", "add", "devRoots", _parent, "--exclude", "home"], config);
        NodeConfiguration afterAdd = NodeConfigurationReader.Read(config).Configuration!;
        var (setExit, set) = await RunAsync(["settings", "set", "supervision.project", "instance"], config);
        NodeConfiguration afterSet = NodeConfigurationReader.Read(config).Configuration!;

        // Assert
        new
        {
            addExit,
            AddOut = add.Stdout,
            Roots = afterAdd.DevRoots,
            Exclude = afterAdd.Discovery.Exclude,
            setExit,
            SetOut = set.Stdout,
            afterSet.Supervision.Project,
            Backup = await File.ReadAllBytesAsync(config + ".bak-20261119T090000Z", TestContext.Current.CancellationToken),
        }.Should().BeEquivalentTo(new
        {
            addExit = 0,
            AddOut = $"settings: devRoots: (none) -> {_parent}\n" +
                "settings: discovery.exclude: (none) -> home\n" +
                "settings: publishes: instance\n" +
                "settings: the node applies this within a minute\n",
            Roots = new[] { _parent },
            Exclude = HomeOnly,
            setExit = 0,
            SetOut = "settings: supervision.project: (none) -> instance\n" +
                "settings: the node applies this within a minute\n",
            Project = "instance",
            Backup = original,
        });
    }

    // AC-9
    [Fact]
    public async Task AddDevRoots_MissingFolder_ExitTwo_FileUnchanged()
    {
        // Arrange
        string config = _harness.CentralNodeJson;
        string missing = Path.Combine(_parent, "does-not-exist");
        string before = await TextOfAsync(config);

        // Act
        var (exit, console) = await RunAsync(["settings", "add", "devRoots", missing], config);

        // Assert
        (exit, console.Stderr, await TextOfAsync(config))
            .Should().Be((2, "zyggy: no such folder: " + missing + "\n", before));
    }

    private static Task<string> TextOfAsync(string path) => File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);

    private string CentralShaped(string? supervisionProject) =>
        NodeJson.Write(
            _harness.Scratch.File("config-central-shaped"),
            CliHarness.Acme,
            CliHarness.Central,
            TestKeys.Acme1,
            busLocalPath: _harness.Bus.SenderDir,
            remote: "https://example.invalid/bus.git",
            secretsRoot: _harness.SecretsRoot,
            devRoots: [_parent],
            discoveryJson: "{ \"exclude\": [\"home\"] }",
            extraTopLevelJson: supervisionProject is null ? null : "\"supervision\": { \"project\": \"" + supervisionProject + "\" }");

    private Task<(int Exit, VerbConsole Console)> RunAsync(string[] args, string config, IReadOnlyDictionary<string, string?>? extra = null) =>
        _harness.RunAsync([.. args, "--config", config], config, extraEnvironment: extra, more: s => s.AddSingleton<TimeProvider>(_clock), ct: TestContext.Current.CancellationToken);
}
