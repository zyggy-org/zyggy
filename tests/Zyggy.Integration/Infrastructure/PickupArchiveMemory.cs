using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Memory;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Integration.Infrastructure;

/// <summary>
/// Central's memory for the item path of the pickup (plan 41 Step 10): a <see cref="MemoryRepoFixture"/> (clone + bare remote, principal
/// <c>acme/alice</c>) seeded with a profile and pushed, an instance directory holding <c>archive.json</c>, and the pickup configuration
/// loaded through the production <see cref="PickupConfiguration.Load"/> from the drop-in's variables plus <c>ZYGGY_INSTANCE_DIR</c>.
/// <see cref="PickupMemory"/> stays as it is for the spec 13 tests (no <c>.git</c>, no archive).
/// </summary>
internal sealed class PickupArchiveMemory : IAsyncLifetime
{
    private readonly string _archiveJson;

    private readonly bool _m365;

    private TestCertificates? _pair;

    /// <param name="archiveJson">The instance's <c>archive.json</c>.</param>
    /// <param name="m365">
    /// Plan 41 Step 12: also give Central's node the offload's M365 instance — <c>m365.json</c> (the golden fixture), a throw-away key pair
    /// under <c>XDG_CONFIG_HOME</c> — and the stub Graph handler over the in-memory OneDrive (read-only: every write is a violation).
    /// </param>
    public PickupArchiveMemory(string archiveJson = "{}", bool m365 = false)
    {
        _archiveJson = archiveJson;
        _m365 = m365;
    }

    public MemoryRepoFixture Repo { get; } = new();

    /// <summary>Gets the in-memory OneDrive with the <c>Zyggy/archive</c> root (plan 41: the laptop's OneDrive client uploaded there).</summary>
    public StubOneDrive Drive { get; } = new();

    /// <summary>Gets the stub Graph handler answering from <see cref="Drive"/> read-only.</summary>
    public StubGraphHandler Graph { get; } = StubGraphHandler.FromGoldenRoutes();

    /// <summary>Gets the pickup's Graph hook: the stub handler, the key-file ownership check only where the test key's mode is set (Linux).</summary>
    public ItemDriveOptions DriveOptions => new() { GraphHandlerFactory = () => Graph, CheckKeyOwnership = OperatingSystem.IsLinux() };

    /// <summary>Gets the throw-away Graph key file (never the real one; the sweep's needle).</summary>
    public string KeyFile => _pair!.KeyFile;

    public string InstanceDir => Path.Combine(Repo.RootDir, "instance");

    public string HomeDir => Path.Combine(Repo.RootDir, "home");

    public MemoryPaths Paths => new(Repo.CloneDir, PickupMemory.Alice);

    public async ValueTask InitializeAsync()
    {
        await Repo.InitializeAsync();
        Directory.CreateDirectory(InstanceDir);
        Directory.CreateDirectory(HomeDir);
        await File.WriteAllTextAsync(Path.Combine(InstanceDir, "archive.json"), _archiveJson);
        Directory.CreateDirectory(Paths.InboxDirectory);
        await File.WriteAllTextAsync(Paths.Profile, "---\nname: profile\ndescription: who alice is\nupdated: 2026-10-01\n---\n- Alice works on calizr\n");
        await Repo.GitAsync(Repo.CloneDir, ["add", "--", "acme/alice/profile.md"], CancellationToken.None);
        await Repo.GitAsync(Repo.CloneDir, ["commit", "-q", "-m", "seed"], CancellationToken.None);
        await Repo.GitAsync(Repo.CloneDir, ["push", "-q", "origin", "HEAD:main"], CancellationToken.None);
        Graph.ReadDrive(Drive);
        if (_m365)
        {
            // The in-process node resolves IANA ids only on Linux here: elsewhere the instance's zone is UTC (the OffloadHarness rule).
            string config = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "golden", "m365", "fixtures", "m365.json"));
            await File.WriteAllTextAsync(
                Path.Combine(InstanceDir, "m365.json"),
                OperatingSystem.IsLinux() ? config : config.Replace("\"Europe/Brussels\"", "\"UTC\"", StringComparison.Ordinal));
            _pair = TestCertificates.Create(Path.Combine(HomeDir, ".config", "zyggy"));
        }
    }

    public async ValueTask DisposeAsync()
    {
        Graph.Violations.Should().BeEmpty();
        _pair?.Dispose();
        await Repo.DisposeAsync();
    }

    /// <summary>The drop-in's five variables, <c>ZYGGY_INSTANCE_DIR</c> and a <c>HOME</c> under the fixture (deterministic deny list).</summary>
    public Dictionary<string, string?> Env(bool withInstanceDir = true)
    {
        var env = new Dictionary<string, string?>
        {
            ["ZYGGY_MEMORY_ROOT"] = Repo.CloneDir,
            ["ZYGGY_TENANT"] = "acme",
            ["ZYGGY_USER"] = "alice",
            ["ZYGGY_TIMEZONE"] = "UTC",
            ["ZYGGY_SECRET_PATTERNS"] = PickupMemory.PatternsPath,
            ["HOME"] = HomeDir,
            ["USERPROFILE"] = HomeDir,

            // Every variable that could place a Graph key elsewhere is inside the fixture or removed (the M365InstanceFixture.Env rule).
            ["XDG_CONFIG_HOME"] = Path.Combine(HomeDir, ".config"),
            ["XDG_STATE_HOME"] = Path.Combine(HomeDir, ".local", "state"),
            ["CREDENTIALS_DIRECTORY"] = null,
        };
        if (withInstanceDir)
        {
            env["ZYGGY_INSTANCE_DIR"] = InstanceDir;
        }

        return env;
    }

    /// <summary>The pickup configuration as the node loads it.</summary>
    public PickupConfigurationResult Configuration(NodeConfiguration node, bool withInstanceDir = true)
    {
        PickupConfigurationResult result = PickupConfiguration.Load(Env(withInstanceDir), node);
        return result.Error is null ? result : throw new InvalidOperationException("pickup configuration: " + result.Error);
    }

    /// <summary>The principal-relative file's text in the clone.</summary>
    public string Text(string relative) => File.ReadAllText(Path.Combine(Repo.PrincipalDir, relative));

    public bool Exists(string relative) => File.Exists(Path.Combine(Repo.PrincipalDir, relative));
}
