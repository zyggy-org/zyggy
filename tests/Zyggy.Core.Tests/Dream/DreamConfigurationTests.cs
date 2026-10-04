using Zyggy.Core.Dream;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Dream;

public sealed class DreamConfigurationTests : IDisposable
{
    private readonly MemoryTree _tree = new(("profile.md", "- [stated] 2026-09-18: Alice.\n"));
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

    public static bool IsLinux => OperatingSystem.IsLinux();

    public void Dispose() => _tree.Dispose();

    private static string Patterns => Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt");

    private Dictionary<string, string?> Env() => new()
    {
        ["ZYGGY_MEMORY_ROOT"] = _tree.Root,
        ["ZYGGY_TENANT"] = "acme",
        ["ZYGGY_USER"] = "alice",
        ["ZYGGY_SECRET_PATTERNS"] = Patterns,
        ["ZYGGY_STATE_DIR"] = Path.Combine(_tree.Root, "state"),
    };

    private DreamConfigurationResult Load(Dictionary<string, string?> env) =>
        DreamConfiguration.Load(env, "1.2.3", path => _files.TryGetValue(path, out var text) ? text : null);

    [Fact]
    public void Load_Complete_ReturnsEnvironmentAndDefaults()
    {
        // Act
        var result = Load(Env());

        // Assert
        result.Error.Should().BeNull();
        result.Environment!.Paths.PrincipalDirectory.Should().Be(_tree.PrincipalDirectory);
        result.Environment.TimeZone.Should().Be(TimeZoneInfo.Utc);
        result.Environment.Version.Should().Be("1.2.3");
        result.Environment.InstanceDirectory.Should().BeNull();
        result.Options.Should().Be(new DreamOptions());
    }

    [Theory]
    [InlineData("ZYGGY_MEMORY_ROOT")]
    [InlineData("ZYGGY_TENANT")]
    [InlineData("ZYGGY_USER")]
    public void Load_RequiredKeyMissing_ErrorNamesKey(string key)
    {
        // Arrange
        var env = Env();
        env.Remove(key);

        // Act
        var result = Load(env);

        // Assert
        result.ErrorKey.Should().Be(key);
        result.Error.Should().Contain(key);
        result.Environment.Should().BeNull();
    }

    [Fact]
    public void Load_SecretPatternsMissing_Error()
    {
        // Arrange
        var env = Env();
        env["ZYGGY_SECRET_PATTERNS"] = Path.Combine(_tree.Root, "none.txt");

        // Act
        var result = Load(env);

        // Assert
        result.ErrorKey.Should().Be("ZYGGY_SECRET_PATTERNS");
    }

    [Fact]
    public void Load_DreamJsonAboveCeiling_ErrorNamesKey()
    {
        // Arrange
        var env = Env();
        var instance = Path.Combine(_tree.Root, "instance");
        env["ZYGGY_INSTANCE_DIR"] = instance;
        _files[Path.Combine(instance, "zyggy.json")] = "{}";
        _files[Path.Combine(instance, "dream.json")] = "{ \"batchMaxLines\": 500 }";

        // Act
        var result = Load(env);

        // Assert
        result.ErrorKey.Should().Be("batchMaxLines");
    }

    [Fact]
    public void Load_DreamJsonTightens_Applied()
    {
        // Arrange
        var env = Env();
        var instance = Path.Combine(_tree.Root, "instance");
        env["ZYGGY_INSTANCE_DIR"] = instance;
        _files[Path.Combine(instance, "zyggy.json")] = "{}";
        _files[Path.Combine(instance, "dream.json")] = "{ \"batchMaxLines\": 50, \"callTimeoutMinutes\": 1, \"runMaxBudgetUsd\": 20.5, \"model\": \"sonnet\" }";

        // Act
        var result = Load(env);

        // Assert
        result.Error.Should().BeNull();
        result.Options!.BatchMaxLines.Should().Be(50);
        result.Options.CallTimeoutMinutes.Should().Be(1);
        result.Options.RunMaxBudgetUsd.Should().Be(20.5m);
        result.Options.Model.Should().Be("sonnet");
        result.Environment!.InstanceDirectory.Should().Be(instance);
    }

    [Theory]
    [InlineData("{ \"batchMaxLine\": 50 }", "batchMaxLine")]
    [InlineData("{ \"batchMaxLines\": \"many\" }", "batchMaxLines")]
    [InlineData("not json", "dream.json")]
    public void Load_DreamJsonInvalid_ErrorNamesKey(string json, string key)
    {
        // Arrange
        var env = Env();
        var instance = Path.Combine(_tree.Root, "instance");
        env["ZYGGY_INSTANCE_DIR"] = instance;
        _files[Path.Combine(instance, "zyggy.json")] = "{}";
        _files[Path.Combine(instance, "dream.json")] = json;

        // Act
        var result = Load(env);

        // Assert
        result.ErrorKey.Should().Be(key);
    }

    [Fact]
    public void Load_UnknownTimeZone_Error()
    {
        // Arrange
        var env = Env();
        env["ZYGGY_TIMEZONE"] = "Mars/Olympus_Mons";

        // Act
        var result = Load(env);

        // Assert
        result.ErrorKey.Should().Be("ZYGGY_TIMEZONE");
    }

    [Fact(Skip = "IANA ids resolve without ICU only on Linux", SkipUnless = nameof(IsLinux))]
    public void Load_IanaTimeZone_OnLinux_Resolves()
    {
        // Arrange
        var env = Env();
        env["ZYGGY_TIMEZONE"] = "Europe/Brussels";

        // Act
        var result = Load(env);

        // Assert
        result.Error.Should().BeNull();
        result.Environment!.TimeZone.Id.Should().Be("Europe/Brussels");
    }

    [Fact]
    public void Load_NoInstanceDir_NoPin()
    {
        // Act
        var result = Load(Env());

        // Assert
        result.Environment!.InstanceDirectory.Should().BeNull();
        result.PinJson.Should().BeNull();
    }

    [Fact]
    public void Load_InstanceDirWithoutPin_Error()
    {
        // Arrange
        var env = Env();
        env["ZYGGY_INSTANCE_DIR"] = Path.Combine(_tree.Root, "instance");

        // Act
        var result = Load(env);

        // Assert
        result.ErrorKey.Should().Be("zyggy.json");
    }

    [Fact]
    public void Load_SecretPatternsDefault_FromInstanceDir()
    {
        // Arrange
        var env = Env();
        env.Remove("ZYGGY_SECRET_PATTERNS");
        var instance = Path.Combine(_tree.Root, "central", "instance");
        var hooks = Path.Combine(_tree.Root, "central", ".claude", "hooks");
        Directory.CreateDirectory(hooks);
        File.Copy(Patterns, Path.Combine(hooks, "secret-patterns.txt"));
        env["ZYGGY_INSTANCE_DIR"] = instance;
        _files[Path.Combine(instance, "zyggy.json")] = "{}";

        // Act
        var result = Load(env);

        // Assert
        result.Error.Should().BeNull();
        Path.GetFullPath(result.Environment!.SecretPatternsPath).Should().Be(Path.GetFullPath(Path.Combine(hooks, "secret-patterns.txt")));
        result.PinJson.Should().Be("{}");
    }
}
