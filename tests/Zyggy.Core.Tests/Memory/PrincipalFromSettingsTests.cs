using Zyggy.Core.Memory;

namespace Zyggy.Core.Tests.Memory;

/// <summary>The settings fallback 33 built for the m365 verbs, moved to Memory so the Hub shares it (spec 11, D1).</summary>
public sealed class PrincipalFromSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zyggy-ut", Guid.NewGuid().ToString("N"));

    public PrincipalFromSettingsTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Apply_OnlyUnsetKeysAndOnlyThose()
    {
        // Arrange
        var settings = Path.Combine(_root, "settings.local.json");
        File.WriteAllText(settings, """
            {"env":{"ZYGGY_MEMORY_ROOT":"/data/memory","ZYGGY_TENANT":"globex","ZYGGY_USER":"bob","ZYGGY_TIMEZONE":"Europe/Brussels",
                    "ZYGGY_HOOKS":"off","ZYGGY_NOW":"2026-01-01T00:00:00Z"}}
            """);
        var env = Env(("ZYGGY_TENANT", "acme"), ("ZYGGY_USER", ""));

        // Act
        var merged = PrincipalFromSettings.Apply(env, settings);

        // Assert
        merged["ZYGGY_MEMORY_ROOT"].Should().Be("/data/memory");
        merged["ZYGGY_TENANT"].Should().Be("acme");
        merged["ZYGGY_USER"].Should().Be("bob");
        merged["ZYGGY_TIMEZONE"].Should().Be("Europe/Brussels");
        merged.Should().NotContainKey("ZYGGY_HOOKS");
        merged.Should().NotContainKey("ZYGGY_NOW");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{")]
    [InlineData("""{"env":{"ZYGGY_TENANT":7}}""")]
    [InlineData("""{"env":"x"}""")]
    [InlineData("""[]""")]
    public void Apply_NoFileBadJsonOrNonString_Unchanged(string? content)
    {
        // Arrange
        var settings = Path.Combine(_root, "settings.local.json");
        if (content is not null)
        {
            File.WriteAllText(settings, content);
        }

        // Act
        var merged = PrincipalFromSettings.Apply(Env(("HOME", "/h")), settings);

        // Assert
        merged.Should().BeEquivalentTo(Env(("HOME", "/h")));
    }

    [Fact]
    public void SettingsPath_ProjectDirectory_ClaudeSettingsLocalJson()
    {
        // Act
        var path = PrincipalFromSettings.SettingsPath(_root);

        // Assert
        path.Should().Be(Path.Join(_root, ".claude", "settings.local.json"));
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
