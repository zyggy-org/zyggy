using System.Runtime.Versioning;

namespace Zyggy.Integration.Infrastructure;

/// <summary>
/// A temporary Central for the m365 verbs: a checkout with <c>instance/m365.json</c> and <c>.claude/hooks/secret-patterns.txt</c> copied
/// from the goldens, a memory tree <c>acme/alice</c>, a <c>HOME</c> with <c>.local/bin</c>, a state directory and a run directory under
/// the download root. Deleted on dispose.
/// </summary>
public sealed class M365InstanceFixture : IDisposable
{
    private readonly ScratchDirectory _scratch = new();

    public M365InstanceFixture()
    {
        Directory.CreateDirectory(Path.Combine(Checkout, ".claude", "hooks"));
        Directory.CreateDirectory(InstanceDirectory);
        File.Copy(Golden("m365", "fixtures", "m365.json"), Path.Combine(InstanceDirectory, "m365.json"));
        File.Copy(Golden("secret-patterns", "secret-patterns.txt"), Path.Combine(Checkout, ".claude", "hooks", "secret-patterns.txt"));
        Directory.CreateDirectory(Path.Combine(MemoryRoot, "acme", "alice"));
        Directory.CreateDirectory(UserBin);
        Directory.CreateDirectory(RunDirectory);
    }

    public string Root => _scratch.Path;

    public string Checkout => Path.Combine(Root, "checkout");

    public string InstanceDirectory => Path.Combine(Checkout, "instance");

    public string MemoryRoot => Path.Combine(Root, "memory");

    public string Home => Path.Combine(Root, "home");

    public string UserBin => Path.Combine(Home, ".local", "bin");

    public string StateDirectory => Path.Combine(Root, "state");

    public string RunDirectory => Path.Combine(Home, ".cache", "zyggy-m365-downloads", "run.test01");

    public static string Golden(params string[] parts) => Path.Combine([AppContext.BaseDirectory, "golden", .. parts]);

    /// <summary>
    /// The variables for <see cref="ZyggyCli"/>: the principal, the instance, <c>HOME</c>, the state and run directories. Every variable
    /// that can place the key, the certificate or a credential is set inside the fixture or removed — a CI runner's own
    /// <c>XDG_CONFIG_HOME</c> or <c>CREDENTIALS_DIRECTORY</c> must never reach a test (a key found there would let a binary test mint
    /// against the real login host).
    /// </summary>
    public Dictionary<string, string?> Env() => new()
    {
        ["ZYGGY_MEMORY_ROOT"] = MemoryRoot,
        ["ZYGGY_TENANT"] = "acme",
        ["ZYGGY_USER"] = "alice",
        ["ZYGGY_TIMEZONE"] = "UTC",
        ["ZYGGY_INSTANCE_DIR"] = InstanceDirectory,
        ["ZYGGY_STATE_DIR"] = StateDirectory,
        ["ZYGGY_M365_RUN_DIR"] = RunDirectory,
        ["HOME"] = Home,
        ["XDG_CONFIG_HOME"] = Path.Combine(Home, ".config"),
        ["XDG_STATE_HOME"] = Path.Combine(Home, ".local", "state"),
        ["CREDENTIALS_DIRECTORY"] = null,
    };

    /// <summary>Writes an executable script (mode 0755) and returns its path.</summary>
    [SupportedOSPlatform("linux")]
    public static string WriteScript(string path, string body)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, body.ReplaceLineEndings("\n"));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return path;
    }

    public void Dispose() => _scratch.Dispose();
}
