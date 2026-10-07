using System.Text;

using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.LinkedIn;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.LinkedIn;

/// <summary>
/// A temporary Central for the LinkedIn verbs: <c>HOME</c>, <c>XDG_CONFIG_HOME</c>, the state directory, an instance directory with the
/// golden <c>linkedin.json</c>, the memory tree <c>acme/alice</c> in <c>Europe/Brussels</c> (a custom +02:00 zone), and a fake clock at
/// 2026-10-07 08:00 UTC. Every value is synthetic. Deleted on dispose.
/// </summary>
internal sealed class LinkedInFixture : IDisposable
{
    public const string AccessToken = "AQV-test-0001-access-token-value";
    public const string ClientSecret = "client-secret-test-0001";

    public static readonly DateTimeOffset Now = new(2026, 10, 7, 8, 0, 0, TimeSpan.Zero);

    private readonly MemoryTree _memory = new();

    public LinkedInFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "zyggy-ut", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(InstanceDirectory);
        File.Copy(Path.Combine(Golden.Directory, "linkedin", "linkedin.json"), InstanceFile);
        Environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["HOME"] = Path.Combine(Root, "home"),
            ["XDG_CONFIG_HOME"] = Path.Combine(Root, "config"),
            ["ZYGGY_STATE_DIR"] = Path.Combine(Root, "state"),
            ["ZYGGY_INSTANCE_DIR"] = InstanceDirectory,
            ["ZYGGY_MEMORY_ROOT"] = _memory.Root,
            ["ZYGGY_TENANT"] = "acme",
            ["ZYGGY_USER"] = "alice",
            ["ZYGGY_TIMEZONE"] = "Europe/Brussels",
        };
        Paths = new LinkedInPaths(Environment);
    }

    public string Root { get; }

    public string InstanceDirectory => Path.Combine(Root, "instance");

    public string InstanceFile => Path.Combine(InstanceDirectory, "linkedin.json");

    public Dictionary<string, string?> Environment { get; }

    public LinkedInPaths Paths { get; }

    public MemoryTree Memory => _memory;

    public FakeTimeProvider Clock { get; } = new(Now);

    // Europe/Brussels in October is CEST (+02:00); IANA ids do not resolve off Linux in an invariant-globalization build.
    public static TimeZoneInfo FindTimeZone(string id) =>
        id == "Europe/Brussels"
            ? TimeZoneInfo.CreateCustomTimeZone("Europe/Brussels", TimeSpan.FromHours(2), "Test/Brussels", "Test/Brussels")
            : TimeZoneInfo.FindSystemTimeZoneById(id);

    public static LinkedInToken Token(DateTimeOffset expiresAt, string scope = "openid,profile,w_member_social", string sub = "sub-alice-0001") =>
        new(1, AccessToken, expiresAt, scope, sub, "Alice Example", Now);

    public LinkedInVerbHost Host(HttpMessageHandler? handler = null, bool checkOwnership = false, TimeSpan? httpTimeout = null) =>
        new(Environment, Clock, FindTimeZone, handler, checkOwnership, httpTimeout);

    public void WriteInstance(string json) => File.WriteAllText(InstanceFile, json, new UTF8Encoding(false));

    public void WriteToken(LinkedInToken token)
    {
        Directory.CreateDirectory(Paths.ConfigDirectory);
        File.WriteAllBytes(Paths.TokenFile, token.ToBytes());
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Paths.TokenFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public void WriteClientSecret(string content = ClientSecret + "\n")
    {
        Directory.CreateDirectory(Paths.ConfigDirectory);
        File.WriteAllText(Paths.ClientSecretFile, content, new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Paths.ClientSecretFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    /// <summary>Every file under the fixture's root except the credential directory, as text, for the secret scan.</summary>
    public string EverythingOutsideCredentials()
    {
        var text = new StringBuilder();
        foreach (var root in new[] { Root, _memory.Root })
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (!file.StartsWith(Paths.ConfigDirectory, StringComparison.Ordinal))
                {
                    text.Append(File.ReadAllText(file)).Append('\n');
                }
            }
        }

        return text.ToString();
    }

    public void Dispose()
    {
        _memory.Dispose();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A leaked temp directory is harmless.
        }
    }
}
