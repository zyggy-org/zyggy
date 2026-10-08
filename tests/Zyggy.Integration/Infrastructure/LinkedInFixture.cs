using System.Text;

namespace Zyggy.Integration.Infrastructure;

/// <summary>
/// A temporary Central for the LinkedIn verbs: <c>HOME</c>, <c>XDG_CONFIG_HOME</c>, a state directory, a checkout whose instance has the
/// golden <c>linkedin.json</c> and whose <c>.claude/hooks</c> has the golden secret patterns, and a memory tree <c>acme/alice</c> (UTC: the
/// binary resolves IANA ids only on Linux). Every value is synthetic. Deleted on dispose.
/// </summary>
internal sealed class LinkedInFixture : IDisposable
{
    public const string AccessToken = "AQV-test-0001-access-token-value";
    public const string ClientSecret = "client-secret-test-0001";
    public const string Code = "code-test-0001-authorization-code";
    public const string Redirect = "https://localhost/zyggy/linkedin";

    private readonly ScratchDirectory _scratch = new();

    public LinkedInFixture()
    {
        Directory.CreateDirectory(InstanceDirectory);
        Directory.CreateDirectory(Path.Combine(Checkout, ".claude", "hooks"));
        File.Copy(M365InstanceFixture.Golden("linkedin", "linkedin.json"), Path.Combine(InstanceDirectory, "linkedin.json"));
        File.Copy(M365InstanceFixture.Golden("secret-patterns", "secret-patterns.txt"), SecretPatterns);
        Directory.CreateDirectory(PrincipalDirectory);
        Directory.CreateDirectory(Home);
    }

    public string Root => _scratch.Path;

    public string Checkout => Path.Combine(Root, "checkout");

    public string InstanceDirectory => Path.Combine(Checkout, "instance");

    public string SecretPatterns => Path.Combine(Checkout, ".claude", "hooks", "secret-patterns.txt");

    public string MemoryRoot => Path.Combine(Root, "memory");

    public string PrincipalDirectory => Path.Combine(MemoryRoot, "acme", "alice");

    public string Home => Path.Combine(Root, "home");

    public string ConfigHome => Path.Combine(Root, "config");

    public string StateRoot => Path.Combine(Root, "state");

    public string StateDirectory => Path.Combine(StateRoot, "linkedin");

    public string CredentialDirectory => Path.Combine(ConfigHome, "zyggy", "linkedin");

    public string TokenFile => Path.Combine(CredentialDirectory, "token.json");

    public string ClientSecretFile => Path.Combine(CredentialDirectory, "client-secret");

    public string PendingFile => Path.Combine(StateDirectory, "pending-auth.json");

    public string CredentialsDirectory => Path.Combine(Root, "credentials");

    /// <summary>The variables for <see cref="ZyggyCli"/>; every variable that could place a credential outside the fixture is removed.</summary>
    public Dictionary<string, string?> Env(int stubPort) => new()
    {
        ["ZYGGY_MEMORY_ROOT"] = MemoryRoot,
        ["ZYGGY_TENANT"] = "acme",
        ["ZYGGY_USER"] = "alice",
        ["ZYGGY_TIMEZONE"] = "UTC",
        ["ZYGGY_INSTANCE_DIR"] = InstanceDirectory,
        ["ZYGGY_STATE_DIR"] = StateRoot,
        ["ZYGGY_SECRET_PATTERNS"] = SecretPatterns,
        ["ZYGGY_LINKEDIN_API_BASE"] = $"http://127.0.0.1:{stubPort}",
        ["HOME"] = Home,
        ["XDG_CONFIG_HOME"] = ConfigHome,
        ["CREDENTIALS_DIRECTORY"] = null,
        ["ZYGGY_HOOKS"] = null,
    };

    /// <summary>Writes the client secret file (one line, 0600 in a 0700 directory off Windows).</summary>
    public void WriteClientSecret(string value = ClientSecret)
    {
        Directory.CreateDirectory(CredentialDirectory);
        File.WriteAllText(ClientSecretFile, value + "\n", new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(CredentialDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(ClientSecretFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    /// <summary>Every file under the fixture except the credential directory, as text, for the secret scan.</summary>
    public string EverythingOutsideCredentials()
    {
        var text = new StringBuilder();
        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        {
            if (!file.StartsWith(CredentialDirectory, StringComparison.Ordinal) && !file.StartsWith(CredentialsDirectory, StringComparison.Ordinal))
            {
                text.Append(File.ReadAllText(file)).Append('\n');
            }
        }

        return text.ToString();
    }

    public void Dispose() => _scratch.Dispose();
}
