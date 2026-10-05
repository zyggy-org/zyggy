using System.Text.RegularExpressions;

namespace Zyggy.Core.M365;

/// <summary>
/// Every Microsoft 365 path on Central (spec 33 Configuration): the state directory <c>&lt;ZYGGY_STATE_DIR&gt;/m365</c> (default
/// <c>~/.local/state/zyggy/m365</c>, the shell's path on Central), its closed set of file names, the download root and the
/// application key and certificate. The only builder of m365 paths.
/// </summary>
internal sealed partial class M365Paths
{
    public M365Paths(IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        string? Get(string key) => environment.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value) ? value : null;

        var home = Get("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        StateDirectory = Path.Join(Get("ZYGGY_STATE_DIR") ?? Path.Join(home, ".local", "state", "zyggy"), "m365");
        DownloadRoot = Path.Join(home, ".cache", "zyggy-m365-downloads");
        var config = Path.Join(Get("XDG_CONFIG_HOME") ?? Path.Join(home, ".config"), "zyggy");
        KeyFile = Get("ZYGGY_M365_KEY_FILE") ?? Path.Join(config, "m365-app.key");
        CertificateFile = Get("ZYGGY_M365_CER_FILE") ?? Path.Join(config, "m365-app.cer");
    }

    /// <summary>Gets the state directory, 0700 on Linux.</summary>
    public string StateDirectory { get; }

    /// <summary>Gets the root of the per-run download directories.</summary>
    public string DownloadRoot { get; }

    /// <summary>Gets the application's private key file.</summary>
    public string KeyFile { get; }

    /// <summary>Gets the application's certificate file.</summary>
    public string CertificateFile { get; }

    /// <summary>Returns a state file; only the shell's file name shapes are accepted.</summary>
    /// <exception cref="ArgumentException">Thrown for any other name.</exception>
    public string StateFile(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return StateFileName().IsMatch(name)
            ? Path.Join(StateDirectory, name)
            : throw new ArgumentException($"'{name}' is not an m365 state file name.", nameof(name));
    }

    [GeneratedRegex(
        @"\A(mail-watermark|(files-)?backfill-[A-Za-z0-9!_=-]{1,200}\.watermark|drive-[A-Za-z0-9!_=-]{1,200}\.token|replied-[0-9]{4}-[0-9]{2}-[0-9]{2}\.ids|actions\.jsonl|brief\.jsonl|brief-[0-9]{4}-[0-9]{2}-[0-9]{2}\.json|mail-backfill\.json|files-backfill\.json)\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex StateFileName();
}
