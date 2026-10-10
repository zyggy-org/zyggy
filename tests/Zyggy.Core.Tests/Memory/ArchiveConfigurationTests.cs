using Zyggy.Core.Memory;

namespace Zyggy.Core.Tests.Memory;

public sealed class ArchiveConfigurationTests
{
    private static readonly string Instance = Path.Combine(Path.GetTempPath(), "zyggy-ut-archive-config", "instance");

    private static readonly string InstanceFile = Path.Combine(Instance, "archive.json");

    public static TheoryData<string, string> Invalid() => new()
    {
        { "not json", "archive.json" },
        { "[1]", "archive.json" },
        { """{"allowed_types": ["text/plain", "text/html"]}""", "allowed_types" },
        { """{"item_max_bytes": "big"}""", "item_max_bytes" },
        { """{"foo": 1}""", "foo" },
        { """{"item_max_bytes": 99999999999}""", "item_max_bytes" },
    };

    [Fact]
    public void Load_NoInstanceDirNoVariable_Defaults()
    {
        // Act
        var result = ArchiveConfiguration.Load(Env(), _ => throw new InvalidOperationException("no file may be read"));

        // Assert
        result.Error.Should().BeNull();
        result.Options.Should().BeEquivalentTo(new ArchiveOptions());
    }

    [Fact]
    public void Load_FileMissing_Defaults()
    {
        // Act
        var result = ArchiveConfiguration.Load(Env(("ZYGGY_INSTANCE_DIR", Instance)), path => path == InstanceFile ? null : throw new InvalidOperationException(path));

        // Assert
        result.Error.Should().BeNull();
        result.Options.Should().BeEquivalentTo(new ArchiveOptions());
    }

    [Fact]
    public void Load_ZyggyArchiveConfigOverridesInstanceFile()
    {
        // Arrange
        var explicitFile = Path.Combine(Path.GetTempPath(), "zyggy-ut-archive-config", "explicit.json");
        var env = Env(("ZYGGY_INSTANCE_DIR", Instance), ("ZYGGY_ARCHIVE_CONFIG", explicitFile));

        // Act
        var result = ArchiveConfiguration.Load(env, path => path == explicitFile ? """{"item_max_bytes": 2048}""" : throw new InvalidOperationException(path));

        // Assert
        result.Error.Should().BeNull();
        result.Options!.ItemMaxBytes.Should().Be(2048);
    }

    [Fact]
    public void Load_Tightens_Applied()
    {
        // Act
        var result = ArchiveConfiguration.Load(
            Env(("ZYGGY_INSTANCE_DIR", Instance)),
            _ => """{"allowed_types": ["text/plain", "application/pdf"], "item_max_bytes": 1048576}""");

        // Assert
        result.Error.Should().BeNull();
        result.Options!.AllowedTypes.Should().BeEquivalentTo([ArchiveMediaType.TextPlain, ArchiveMediaType.Pdf]);
        result.Options.ItemMaxBytes.Should().Be(1_048_576);
        result.Options.ProjectMaxBytes.Should().Be(52_428_800);
    }

    [Theory]
    [MemberData(nameof(Invalid))]
    public void Load_Invalid_NamesKey(string json, string key)
    {
        // Act
        var result = ArchiveConfiguration.Load(Env(("ZYGGY_INSTANCE_DIR", Instance)), _ => json);

        // Assert
        result.Options.Should().BeNull();
        result.ErrorKey.Should().Be(key);
        result.Error.Should().StartWith(key);
    }

    [Fact]
    public void Load_SourceDenyTilde_ExpandsWithHome()
    {
        // Arrange
        var home = Path.Combine(Path.GetTempPath(), "zyggy-ut-home");

        // Act
        var result = ArchiveConfiguration.Load(
            Env(("ZYGGY_INSTANCE_DIR", Instance), ("HOME", home)),
            _ => """{"source_deny": ["~/Documents/private"]}""");

        // Assert
        result.Error.Should().BeNull();
        result.Options!.SourceDeny.Should().Equal(Path.Combine(home, "Documents", "private"));
    }

    private static Dictionary<string, string?> Env(params (string Key, string Value)[] pairs)
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, value) in pairs)
        {
            env[key] = value;
        }

        return env;
    }
}
