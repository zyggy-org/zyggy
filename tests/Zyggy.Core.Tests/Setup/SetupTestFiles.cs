using System.Globalization;

using Zyggy.Core.Envelope;
using Zyggy.Core.Setup;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Tests.Setup;

/// <summary>
/// A scratch profile for the setup unit tests (plan 42 Step 1): a temp root with <c>config/node.json</c> written from a JSON string, a fake
/// <c>claude.exe</c> (an empty file, executable bit on Linux) in <c>bin/</c> or <c>profile/.local/bin/</c>, and a <see cref="SetupRequest"/>
/// for <c>acme</c>/<c>home-laptop</c> stamped at the fake clock <c>2026-11-04T09:30:00Z</c>. Deleted on dispose.
/// </summary>
internal sealed class SetupTestFiles : IDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 11, 4, 9, 30, 0, TimeSpan.Zero);

    public static readonly TenantId Acme = TenantId.Parse("acme");

    public static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    public const string Stamp = "20261104T093000Z";

    public SetupTestFiles()
    {
        Root = Path.Combine(Path.GetTempPath(), "zyggy-ut", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string ConfigPath => Path.Combine(Root, "config", "node.json");

    public string BackupPath => ConfigPath + ".bak-" + Stamp;

    public string Bin => Path.Combine(Root, "bin");

    public string UserProfile => Path.Combine(Root, "profile");

    public string LocalAppData => Path.Combine(Root, "localappdata");

    public string SecretsRoot => Path.Combine(Root, "secrets");

    /// <summary>The environment the verb sees: <c>PATH</c> of only <see cref="Bin"/>, the profile and the local application data.</summary>
    public Dictionary<string, string?> Environment() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["PATH"] = Bin,
        ["USERPROFILE"] = UserProfile,
        ["HOME"] = UserProfile,
        ["LOCALAPPDATA"] = LocalAppData,
    };

    /// <summary>
    /// The old-spike <c>node.json</c>: tenant, machine, a <c>bus</c> section and a <c>secrets</c> section, no <c>claude</c>; <paramref name="signWith"/>
    /// and <paramref name="extra"/> (top-level <c>"key": value</c> pairs) are added when given.
    /// </summary>
    public string SpikeJson(string? signWith = null, string? extra = null, string tenant = "acme", string machine = "home-laptop", string store = "file", bool withBus = true)
    {
        string bus = Slashes(Path.Combine(Root, "bus"));
        string secrets = Slashes(SecretsRoot);
        string signLine = signWith is null ? string.Empty : $"  \"signWith\": \"{signWith}\",\n";
        string extraLine = extra is null ? string.Empty : $"  {extra},\n";
        string busLine = withBus
            ? $"  \"bus\": {{ \"owner\": \"acme-org\", \"repo\": \"acme-bus\", \"branch\": \"main\", \"localPath\": \"{bus}\", \"remote\": \"https://example.invalid/acme-org/acme-bus.git\" }},\n"
            : string.Empty;
        return string.Create(CultureInfo.InvariantCulture, $$"""
            {
              "tenant": "{{tenant}}",
              "machine": "{{machine}}",
            {{signLine}}{{extraLine}}{{busLine}}  "poll": { "activeSeconds": 10, "idleSeconds": 60 },
              "secrets": { "store": "{{store}}", "root": "{{secrets}}" }
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

    /// <summary>An empty <c>claude.exe</c> (or <paramref name="name"/>) in <see cref="Bin"/>, executable on Linux; returns its absolute path.</summary>
    public string FakeClaudeExe(string name = "claude.exe") => Executable(Path.Combine(Bin, name));

    /// <summary>An empty <c>claude.exe</c> in <c>&lt;UserProfile&gt;/.local/bin/</c>, executable on Linux; returns its absolute path.</summary>
    public string FakeClaudeInLocalBin() => Executable(Path.Combine(UserProfile, ".local", "bin", "claude.exe"));

    /// <summary>The request for <c>acme</c>/<c>home-laptop</c> over this root.</summary>
    public SetupRequest Request(KeyId? signWith = null, string tenant = "acme", string machine = "home-laptop") => new(
        ConfigPath,
        TenantId.Parse(tenant),
        MachineName.Parse(machine),
        signWith,
        Path.Combine(Root, "core"),
        new Uri(SetupRequest.DefaultCoreRemote),
        Path.Combine(UserProfile, ".claude"),
        UserProfile,
        LocalAppData,
        Now);

    public static string Slashes(string path) => path.Replace('\\', '/');

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

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
}
