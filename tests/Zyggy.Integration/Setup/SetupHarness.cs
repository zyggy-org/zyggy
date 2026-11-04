using System.Globalization;
using System.Text;

using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Processes;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Setup;

/// <summary>
/// A temp laptop profile for <c>zyggy setup</c> in process (plan 42 Step 2): <c>profile/</c> (<c>USERPROFILE</c>/<c>HOME</c>), <c>localappdata/</c>
/// (<c>LOCALAPPDATA</c>), <c>bin/claude.exe</c> (an empty file, executable on Linux) on a <c>PATH</c> of only that folder, a <c>node.json</c> from a
/// raw JSON string (the old-spike shapes), a file secret store seeded with <c>acme/1</c> and the test PAT, a local bare <c>zyggy-core</c> stand-in
/// (<see cref="CoreTemplateFixture"/>, Step 4) checked out at <c>profile/agents/zyggy-core</c>, and every process recorded. Never the real profile,
/// the real <c>claude</c>, GitHub or the Credential Manager.
/// </summary>
internal sealed class SetupHarness : IAsyncDisposable
{
    public static readonly TenantId Acme = TenantId.Parse("acme");

    private SetupHarness()
    {
        Scratch = new ScratchDirectory();
        Hooks = TestHost.Hooks();
        Directory.CreateDirectory(Profile);
        Directory.CreateDirectory(LocalAppData);
        ClaudeExe = Executable(Path.Combine(Bin, "claude.exe"));
        Core = new CoreTemplateFixture(Scratch.File("remote"));
    }

    public ScratchDirectory Scratch { get; }

    public HookedProcessRunner Hooks { get; }

    public string Profile => Scratch.File("profile");

    public string LocalAppData => Scratch.File("localappdata");

    public string Bin => Scratch.File("bin");

    public string ClaudeExe { get; }

    public string SecretsRoot => Scratch.File("secrets");

    /// <summary>The local bare <c>zyggy-core</c> stand-in (plan 42 Step 4); every run's <c>--core-remote</c> points at it, never GitHub.</summary>
    public CoreTemplateFixture Core { get; }

    /// <summary>The checkout under the temp profile: <c>profile/agents/zyggy-core</c>.</summary>
    public string CorePath => Path.Combine(Profile, "agents", "zyggy-core");

    /// <summary>The stand-in's <c>file://</c> URL, exactly as given to <c>--core-remote</c>.</summary>
    public string CoreRemote => Core.RemoteUrl;

    public string ConfigPath => Path.Combine(Scratch.File("config"), "node.json");

    /// <summary>A profile whose file store holds <c>acme/1</c> and the PAT (or only what <paramref name="signingKey"/>/<paramref name="pat"/> allow).</summary>
    public static async Task<SetupHarness> CreateAsync(CancellationToken ct, bool signingKey = true, bool pat = true)
    {
        var harness = new SetupHarness();
        await harness.Core.SeedAsync(ct);
        await using ServiceProvider services = new ServiceCollection().AddFileSecretStore(o => o.RootDirectory = harness.SecretsRoot).BuildServiceProvider();
        ISecretStore store = services.GetRequiredService<ISecretStore>();
        if (signingKey)
        {
            await store.SetAsync(Acme, TestKeys.Acme1.SecretName, TestKeys.Secret1, ct);
        }

        if (pat)
        {
            await store.SetAsync(Acme, TestKeys.GitHubPat, Encoding.UTF8.GetBytes(TestKeys.TestPat), ct);
        }

        return harness;
    }

    /// <summary>The old-spike <c>node.json</c>: tenant, machine, bus, poll and a file <c>secrets</c> section; no <c>claude</c>, no <c>signWith</c> unless given.</summary>
    public string OldSpikeJson(string? signWith = null, string tenant = "acme", string machine = "home-laptop")
    {
        string signLine = signWith is null ? string.Empty : $"  \"signWith\": \"{signWith}\",\n";
        return string.Create(CultureInfo.InvariantCulture, $$"""
            {
              "tenant": "{{tenant}}",
              "machine": "{{machine}}",
            {{signLine}}  "bus": {
                "owner": "acme-org",
                "repo": "acme-bus",
                "branch": "main",
                "localPath": "{{Slashes(Scratch.File("bus"))}}",
                "remote": "https://example.invalid/acme-org/acme-bus.git"
              },
              "poll": { "activeSeconds": 10, "idleSeconds": 60 },
              "secrets": { "store": "file", "root": "{{Slashes(SecretsRoot)}}" }
            }

            """);
    }

    /// <summary>Writes <paramref name="json"/> as <see cref="ConfigPath"/> and returns the path.</summary>
    public string WriteNodeJson(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, json);
        return ConfigPath;
    }

    /// <summary>
    /// The arguments of one installer call: <c>setup --config … --tenant … --machine … [--sign-with …] --core-path … --core-remote …</c>; the
    /// remote is the local stand-in unless <paramref name="coreRemote"/> names another (never a network URL).
    /// </summary>
    public string[] Args(string tenant = "acme", string machine = "home-laptop", string? signWith = null, string? coreRemote = null)
    {
        var args = new List<string> { "setup", "--config", ConfigPath, "--tenant", tenant, "--machine", machine };
        if (signWith is not null)
        {
            args.AddRange(["--sign-with", signWith]);
        }

        args.AddRange(["--core-path", CorePath, "--core-remote", coreRemote ?? CoreRemote]);
        return [.. args];
    }

    /// <summary>The environment the run sees: the temp profile and a <c>PATH</c> of only <see cref="Bin"/>.</summary>
    public Dictionary<string, string?> Environment() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["PATH"] = Bin,
        ["USERPROFILE"] = Profile,
        ["HOME"] = Profile,
        ["LOCALAPPDATA"] = LocalAppData,
    };

    /// <summary>Runs <c>zyggy</c> in process with the harness's environment, recorded processes and git isolation.</summary>
    public Task<(int Exit, VerbConsole Console)> RunAsync(string[] args, CancellationToken ct) =>
        ZyggyCliInProcess.RunAsync(args, Environment(), Configure, stdin: null, ct);

    /// <summary>Every file under the scratch root, with its content and last-write time.</summary>
    public IReadOnlyDictionary<string, (string Content, DateTime LastWriteUtc)> WrittenFiles() =>
        Directory.EnumerateFiles(Scratch.Path, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, f => (File.ReadAllText(f), File.GetLastWriteTimeUtc(f)), StringComparer.Ordinal);

    /// <summary>
    /// <see cref="WrittenFiles"/> without git's own bookkeeping (every <c>.git</c> folder, which a fetch touches) and without the remote stand-in:
    /// the files an owner sees — <c>node.json</c>, its backups, the checkout's working tree, the profile.
    /// </summary>
    public IReadOnlyDictionary<string, (string Content, DateTime LastWriteUtc)> WorkingFiles()
    {
        string gitSegment = Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar;
        return WrittenFiles()
            .Where(kv => !kv.Key.Contains(gitSegment, StringComparison.Ordinal) && !kv.Key.StartsWith(Core.RootDir, StringComparison.Ordinal))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }

    /// <summary>
    /// AC-22: no secret value on stdout, stderr, in any recorded process spec, or in any file under the scratch root other than the secret store
    /// itself (which holds them by design).
    /// </summary>
    public void Sweep(VerbConsole console)
    {
        IEnumerable<string?> files = WrittenFiles()
            .Where(kv => !kv.Key.StartsWith(SecretsRoot, StringComparison.Ordinal))
            .Select(kv => (string?)kv.Value.Content);
        SecretSweep.AssertAbsent([console.Stdout, console.Stderr, .. Hooks.Calls.SelectMany(SecretSweep.TextsOf), .. files]);
    }

    public ValueTask DisposeAsync()
    {
        // Git writes its object files read-only; clear that first so the scratch delete does not trip over them on Windows.
        try
        {
            foreach (string file in Directory.EnumerateFiles(Scratch.Path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leaked temp directories are documented in the README.
        }

        Scratch.Dispose();
        return ValueTask.CompletedTask;
    }

    private static string Slashes(string path) => path.Replace('\\', '/');

    private static string Executable(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    private void Configure(IServiceCollection services)
    {
        services.Configure<GitHubBusOptions>(o => o.GitEnvironment = new Dictionary<string, string?>(Core.GitEnvironment));
        services.AddSingleton<IProcessRunner>(Hooks);
    }
}
