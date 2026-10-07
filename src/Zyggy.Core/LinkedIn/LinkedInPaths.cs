namespace Zyggy.Core.LinkedIn;

/// <summary>
/// Every LinkedIn path on Central (spec 36 Files): the state directory <c>&lt;ZYGGY_STATE_DIR&gt;/linkedin</c> (default
/// <c>~/.local/state/zyggy/linkedin</c>) with the action log and the pending sign-in, and the credential directory
/// <c>&lt;XDG_CONFIG_HOME or ~/.config&gt;/zyggy/linkedin</c> with the token and the client secret. The only builder of LinkedIn paths.
/// </summary>
internal sealed class LinkedInPaths
{
    public const string CredentialsDirectorySecretName = "linkedin-client-secret";

    public LinkedInPaths(IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        string? Get(string key) => environment.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value) ? value : null;

        var home = Get("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        StateDirectory = Path.Join(Get("ZYGGY_STATE_DIR") ?? Path.Join(home, ".local", "state", "zyggy"), "linkedin");
        ConfigDirectory = Path.Join(Get("XDG_CONFIG_HOME") ?? Path.Join(home, ".config"), "zyggy", "linkedin");
        CredentialsDirectorySecret = Get("CREDENTIALS_DIRECTORY") is { } credentials ? Path.Join(credentials, CredentialsDirectorySecretName) : null;
    }

    /// <summary>Gets the state directory, 0700 on Linux.</summary>
    public string StateDirectory { get; }

    /// <summary>Gets the action log, one row per <c>publish_post</c> call.</summary>
    public string ActionLog => Path.Join(StateDirectory, "actions.jsonl");

    /// <summary>Gets the pending sign-in written by <c>auth start</c>.</summary>
    public string PendingAuth => Path.Join(StateDirectory, "pending-auth.json");

    /// <summary>Gets the credential directory, 0700 on Linux.</summary>
    public string ConfigDirectory { get; }

    /// <summary>Gets the token file written by <c>auth finish</c>.</summary>
    public string TokenFile => Path.Join(ConfigDirectory, "token.json");

    /// <summary>Gets the client secret file the owner places over SSH.</summary>
    public string ClientSecretFile => Path.Join(ConfigDirectory, "client-secret");

    /// <summary>Gets systemd's copy of the client secret (<c>LoadCredential=</c>), or <see langword="null"/> outside a unit.</summary>
    public string? CredentialsDirectorySecret { get; }
}
