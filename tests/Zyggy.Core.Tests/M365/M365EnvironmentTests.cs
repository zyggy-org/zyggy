using Zyggy.Core.M365;

namespace Zyggy.Core.Tests.M365;

/// <summary>Where the m365 verbs find the instance, its configuration and the principal (spec 33 Configuration).</summary>
public sealed class M365EnvironmentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zyggy-ut", Guid.NewGuid().ToString("N"));

    public M365EnvironmentTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Load_InstanceDirFromEnvThenProjectDir()
    {
        // Arrange
        var instance = Path.Combine(_root, "checkout", "instance");
        var project = Path.Combine(_root, "project");

        // Act
        var fromEnv = M365Environment.Load(Env(("ZYGGY_INSTANCE_DIR", instance), ("CLAUDE_PROJECT_DIR", project)));
        var fromProject = M365Environment.Load(Env(("CLAUDE_PROJECT_DIR", project)));

        // Assert
        fromEnv.Environment!.InstanceDirectory.Should().Be(instance);
        fromEnv.Environment.Checkout.Should().Be(Path.Combine(_root, "checkout"));
        fromEnv.Environment.ConfigPath.Should().Be(Path.Combine(instance, "m365.json"));
        fromEnv.Environment.SettingsPath.Should().Be(Path.Combine(_root, "checkout", ".claude", "settings.local.json"));
        fromEnv.Environment.SecretPatternsPath.Should().Be(Path.Combine(_root, "checkout", ".claude", "hooks", "secret-patterns.txt"));
        fromProject.Environment!.InstanceDirectory.Should().Be(Path.Combine(project, "instance"));
    }

    [Fact]
    public void Load_ExplicitConfigAndSettings_Win()
    {
        // Act
        var load = M365Environment.Load(Env(
            ("ZYGGY_INSTANCE_DIR", Path.Combine(_root, "instance")),
            ("ZYGGY_M365_CONFIG", "/etc/m365.json"),
            ("ZYGGY_M365_SETTINGS", "/etc/settings.json"),
            ("ZYGGY_SECRET_PATTERNS", "/etc/patterns.txt")));

        // Assert
        load.Environment!.ConfigPath.Should().Be("/etc/m365.json");
        load.Environment.SettingsPath.Should().Be("/etc/settings.json");
        load.Environment.SecretPatternsPath.Should().Be("/etc/patterns.txt");
    }

    [Fact]
    public void Load_NoInstanceDir_Error()
    {
        // Act
        var load = M365Environment.Load(Env());

        // Assert
        load.Environment.Should().BeNull();
        load.Error.Should().Be("configuration error: ZYGGY_INSTANCE_DIR is not set (and no CLAUDE_PROJECT_DIR to derive it from)");
    }

    [Theory]
    [InlineData(null, 47365)]
    [InlineData("1024", 1024)]
    [InlineData("65535", 65535)]
    public void Port_InRange_Parsed(string? value, int expected)
    {
        // Act
        var port = M365Environment.Port(value is null ? Env() : Env(("ZYGGY_M365_PORT", value)), out var error);

        // Assert
        port.Should().Be(expected);
        error.Should().BeNull();
    }

    [Theory]
    [InlineData("80")]
    [InlineData("1023")]
    [InlineData("65536")]
    [InlineData("99999")]
    [InlineData("abc")]
    [InlineData("4736a")]
    public void Port_OutOfRange_Error(string value)
    {
        // Act
        var port = M365Environment.Port(Env(("ZYGGY_M365_PORT", value)), out var error);

        // Assert
        port.Should().BeNull();
        error.Should().Be($"configuration error: ZYGGY_M365_PORT '{value}' is not a port in 1024..65535");
    }

    [Fact]
    public void PrincipalFromSettings_OnlyUnsetKeysAndOnlyThose()
    {
        // Arrange
        var settings = Path.Combine(_root, "settings.local.json");
        File.WriteAllText(settings, """
            {"env":{"ZYGGY_MEMORY_ROOT":"/srv/memory","ZYGGY_TENANT":"geoffrey","ZYGGY_USER":"geoffrey","ZYGGY_TIMEZONE":"Europe/Brussels",
                    "ZYGGY_HOOKS":"off","ZYGGY_NOW":"2026-01-01T00:00:00Z"}}
            """);
        var env = Env(("ZYGGY_TENANT", "acme"), ("ZYGGY_USER", ""));

        // Act
        var merged = M365Environment.PrincipalFromSettings(env, settings);

        // Assert
        merged["ZYGGY_MEMORY_ROOT"].Should().Be("/srv/memory");
        merged["ZYGGY_TENANT"].Should().Be("acme");
        merged["ZYGGY_USER"].Should().Be("geoffrey");
        merged["ZYGGY_TIMEZONE"].Should().Be("Europe/Brussels");
        merged.Should().NotContainKey("ZYGGY_HOOKS");
        merged.Should().NotContainKey("ZYGGY_NOW");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{")]
    [InlineData("""{"env":{"ZYGGY_TENANT":7}}""")]
    [InlineData("""{"env":"x"}""")]
    public void PrincipalFromSettings_NoFileBadJsonOrNonString_Unchanged(string? content)
    {
        // Arrange
        var settings = Path.Combine(_root, "settings.local.json");
        if (content is not null)
        {
            File.WriteAllText(settings, content);
        }

        // Act
        var merged = M365Environment.PrincipalFromSettings(Env(("HOME", "/h")), settings);

        // Assert
        merged.Should().BeEquivalentTo(Env(("HOME", "/h")));
    }

    private static Dictionary<string, string?> Env(params (string Key, string? Value)[] pairs)
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, value) in pairs)
        {
            env[key] = value;
        }

        return env;
    }
}
