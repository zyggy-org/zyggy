using Zyggy.Core.Configuration;
using Zyggy.Core.Dream;
using Zyggy.Core.Memory;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Memory;

/// <summary>Spec 13 AC-16: Central's pickup configuration loads with the dream's rules and messages, or names the key that is missing.</summary>
public sealed class PickupConfigurationTests : IDisposable
{
    private static readonly string Patterns = Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt");

    private readonly MemoryTree _tree = new(("profile.md", "---\nname: profile\n---\n"));

    public void Dispose() => _tree.Dispose();

    [Fact]
    public void Load_AllSet_PathsZonePatterns()
    {
        // Arrange
        Dictionary<string, string?> env = Env();
        env.Remove("ZYGGY_TIMEZONE");

        // Act
        PickupConfigurationResult result = PickupConfiguration.Load(env, Node());

        // Assert
        result.Error.Should().BeNull();
        result.Paths!.PrincipalDirectory.Should().Be(_tree.Paths.PrincipalDirectory);
        result.Zone.Should().Be(TimeZoneInfo.Utc);
        result.Patterns!.Count.Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData("ZYGGY_MEMORY_ROOT")]
    [InlineData("ZYGGY_TENANT")]
    [InlineData("ZYGGY_USER")]
    public void Load_MissingVariable_DreamSentence(string key)
    {
        // Arrange
        Dictionary<string, string?> env = Env();
        env.Remove(key);

        // Act
        PickupConfigurationResult result = PickupConfiguration.Load(env, Node());

        // Assert
        result.Error.Should().Be($"{key} is not set");
        result.Paths.Should().BeNull();
        result.Patterns.Should().BeNull();
    }

    [Fact]
    public void Load_RootOrPrincipalDirectoryMissing_DreamSentence()
    {
        // Arrange
        Dictionary<string, string?> noRoot = Env();
        noRoot["ZYGGY_MEMORY_ROOT"] = Path.Combine(_tree.Root, "absent");
        Dictionary<string, string?> noUser = Env();
        noUser["ZYGGY_USER"] = "bob";

        // Act
        string? root = PickupConfiguration.Load(noRoot, Node()).Error;
        string? user = PickupConfiguration.Load(noUser, Node()).Error;

        // Assert
        root.Should().Be($"memory root {Path.Combine(_tree.Root, "absent")} does not exist (ZYGGY_MEMORY_ROOT)");
        user.Should().StartWith("memory directory ").And.EndWith(" does not exist (ZYGGY_TENANT/ZYGGY_USER)");
    }

    [Fact]
    public void Load_TenantDiffersFromNodeJson_TenantSentence()
    {
        // Act
        PickupConfigurationResult result = PickupConfiguration.Load(Env(), Node("other-org"));

        // Assert
        result.Error.Should().Be("ZYGGY_TENANT 'acme' is not node.json's tenant 'other-org'");
    }

    [Fact]
    public void Load_UnknownTimeZone_DreamSentence()
    {
        // Arrange
        Dictionary<string, string?> env = Env();
        env["ZYGGY_TIMEZONE"] = "Mars/Olympus";

        // Act
        PickupConfigurationResult result = PickupConfiguration.Load(env, Node());

        // Assert
        result.Error.Should().Be("ZYGGY_TIMEZONE 'Mars/Olympus' is not a known time zone");
    }

    [Fact]
    public void Load_PatternsUnset_NoInstanceDir_DreamSentence()
    {
        // Arrange
        Dictionary<string, string?> env = Env();
        env.Remove("ZYGGY_SECRET_PATTERNS");

        // Act
        PickupConfigurationResult result = PickupConfiguration.Load(env, Node());

        // Assert
        result.Error.Should().Be("ZYGGY_SECRET_PATTERNS is not set (and no ZYGGY_INSTANCE_DIR to derive it from)");
    }

    [Fact]
    public void Load_PatternsDerivedFromInstanceDir()
    {
        // Arrange
        string instance = Path.Combine(_tree.Root, "checkout", "instance");
        string hooks = Path.Combine(_tree.Root, "checkout", ".claude", "hooks");
        Directory.CreateDirectory(instance);
        Directory.CreateDirectory(hooks);
        File.Copy(Patterns, Path.Combine(hooks, "secret-patterns.txt"));
        Dictionary<string, string?> env = Env();
        env.Remove("ZYGGY_SECRET_PATTERNS");
        env["ZYGGY_INSTANCE_DIR"] = instance;

        // Act
        PickupConfigurationResult result = PickupConfiguration.Load(env, Node());

        // Assert
        result.Error.Should().BeNull();
        result.Patterns.Should().NotBeNull();
    }

    [Fact]
    public void Load_PatternsFileMissing_DreamSentence()
    {
        // Arrange
        Dictionary<string, string?> env = Env();
        string missing = Path.Combine(_tree.Root, "absent.txt");
        env["ZYGGY_SECRET_PATTERNS"] = missing;

        // Act
        PickupConfigurationResult result = PickupConfiguration.Load(env, Node());

        // Assert
        result.Error.Should().Be($"secret patterns {missing} are missing (ZYGGY_SECRET_PATTERNS)");
    }

    [Fact]
    public void Load_PatternsMalformed_SecretPatternsError()
    {
        // Arrange
        string bad = Path.Combine(_tree.Root, "bad.txt");
        File.WriteAllText(bad, "no-tab-here\n");
        Dictionary<string, string?> env = Env();
        env["ZYGGY_SECRET_PATTERNS"] = bad;

        // Act
        PickupConfigurationResult result = PickupConfiguration.Load(env, Node());

        // Assert
        result.Error.Should().Be(SecretPatterns.Load(bad).Error);
        result.Error.Should().NotBeNull();
    }

    [Theory]
    [InlineData("ZYGGY_MEMORY_ROOT", null)]
    [InlineData("ZYGGY_USER", "bob")]
    [InlineData("ZYGGY_TIMEZONE", "Mars/Olympus")]
    [InlineData("ZYGGY_SECRET_PATTERNS", null)]
    [InlineData("ZYGGY_SECRET_PATTERNS", "absent.txt")]
    public void Load_SameInputs_SameDetailAsDreamConfiguration(string key, string? value)
    {
        // Arrange
        Dictionary<string, string?> env = Env();
        env[key] = value is null ? null : key == "ZYGGY_SECRET_PATTERNS" ? Path.Combine(_tree.Root, value) : value;

        // Act
        string? pickup = PickupConfiguration.Load(env, Node()).Error;
        string? dream = DreamConfiguration.Load(env, "0.0.0-test", _ => null).Error;

        // Assert
        pickup.Should().NotBeNull().And.Be(dream);
    }

    private static NodeConfiguration Node(string tenant = "acme") => new(
        TenantId.Parse(tenant),
        MachineName.Central,
        null,
        new BusSettings("acme-org", "acme-bus", "main", "/bus", "https://example.invalid/acme-org/acme-bus.git"),
        new PollSettings(),
        new SecretsSettings());

    private Dictionary<string, string?> Env() => new()
    {
        ["ZYGGY_MEMORY_ROOT"] = _tree.Root,
        ["ZYGGY_TENANT"] = "acme",
        ["ZYGGY_USER"] = "alice",
        ["ZYGGY_TIMEZONE"] = "UTC",
        ["ZYGGY_SECRET_PATTERNS"] = Patterns,
    };
}
