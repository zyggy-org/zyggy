using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Memory;
using Zyggy.Core.Processes;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Integration.Infrastructure;

/// <summary>
/// Plan 38 Step 8: a memory clone seeded with <c>archive-indexed</c> (two indexed items) and its local bare remote, an instance with
/// <c>m365.json</c> and <c>archive.json</c> (offload enabled), a throw-away key and certificate, and the in-memory OneDrive. The offload
/// runs in process with the real process runner (real git); only Graph is the stub — the binary has no test switch for it.
/// </summary>
internal sealed class OffloadHarness : IAsyncLifetime
{
    private TestCertificates? _pair;

    public MemoryRepoFixture Repo { get; } = new();

    internal StubOneDrive Drive { get; } = new();

    internal StubGraphHandler Graph { get; } = StubGraphHandler.FromGoldenRoutes();

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 1, 3, 30, 0, TimeSpan.Zero));

    public string InstanceDir => Path.Combine(Repo.RootDir, "instance");

    public string ActionLogFile => Path.Combine(Repo.StateDir, "m365", "actions.jsonl");

    public string KeyFile => _pair!.KeyFile;

    public IReadOnlyList<string> ActionRows() => File.Exists(ActionLogFile) ? File.ReadAllLines(ActionLogFile) : [];

    public async ValueTask InitializeAsync()
    {
        await Repo.InitializeAsync();
        await Repo.SeedAsync("archive-indexed", TestContext.Current.CancellationToken);
        Graph.UseDrive(Drive);
        Directory.CreateDirectory(InstanceDir);

        // The binary resolves IANA ids only on Linux (invariant globalization): elsewhere the instance's zone is UTC.
        var config = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "golden", "m365", "fixtures", "m365.json"));
        File.WriteAllText(Path.Combine(InstanceDir, "m365.json"), OperatingSystem.IsLinux() ? config : config.Replace("\"Europe/Brussels\"", "\"UTC\"", StringComparison.Ordinal));
        Configure("""{"offload": {"enabled": true}}""");
        _pair = TestCertificates.Create(Path.Combine(Repo.HomeDir, ".config", "zyggy"));
    }

    public async ValueTask DisposeAsync()
    {
        Graph.Violations.Should().BeEmpty();
        _pair?.Dispose();
        await Repo.DisposeAsync();
    }

    public void Configure(string archiveJson) => File.WriteAllText(Path.Combine(InstanceDir, "archive.json"), archiveJson);

    /// <summary>
    /// The archive environment plus the instance, the state directory and <c>XDG_CONFIG_HOME</c> inside the fixture; every variable that
    /// could place a key elsewhere is set here or removed (the <c>M365InstanceFixture.Env</c> rule).
    /// </summary>
    public Dictionary<string, string?> Env(IReadOnlyDictionary<string, string?>? extra = null)
    {
        var env = Repo.ArchiveEnv(extra);
        env["ZYGGY_INSTANCE_DIR"] = InstanceDir;
        env["XDG_CONFIG_HOME"] = Path.Combine(Repo.HomeDir, ".config");
        env["XDG_STATE_HOME"] = Path.Combine(Repo.HomeDir, ".local", "state");
        env["CREDENTIALS_DIRECTORY"] = null;
        foreach (var (key, value) in extra ?? new Dictionary<string, string?>())
        {
            env[key] = value;
        }

        return env;
    }

    public async Task<(int Exit, VerbConsole Console)> RunInProcessAsync(params string[] args)
    {
        var console = new VerbConsole();
        var verb = new ArchiveVerb(Env(), Clock, FindTimeZone, new ProcessRunner(TimeProvider.System), Graph, checkKeyOwnership: OperatingSystem.IsLinux());
        var exit = await verb.RunAsync(["offload", .. args], console.Io, TestContext.Current.CancellationToken);
        return (exit, console);
    }

    private static TimeZoneInfo FindTimeZone(string id) => id == "UTC" ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(id);
}
