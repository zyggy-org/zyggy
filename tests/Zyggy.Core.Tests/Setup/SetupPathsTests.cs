using Zyggy.Core.Setup;

namespace Zyggy.Core.Tests.Setup;

/// <summary>Spec 42 AC-16, plan 42 A5: every setup path is resolved and built in one place.</summary>
public sealed class SetupPathsTests
{
    private static readonly string Profile = Path.Combine(Path.GetTempPath(), "zyggy-ut", "profile");

    [Theory]
    [InlineData("from-option", "from-variable", "from-option")]
    [InlineData(null, "from-variable", "from-variable")]
    [InlineData("", "from-variable", "from-variable")]
    [InlineData(null, null, "profile")]
    [InlineData(null, "", "profile")]
    public void ResolveClaudeDir_OptionThenVariableThenProfile(string? option, string? variable, string expected)
    {
        // Arrange
        var environment = new Dictionary<string, string?> { ["CLAUDE_CONFIG_DIR"] = variable, ["USERPROFILE"] = Profile, ["HOME"] = Profile };

        // Act
        string resolved = SetupPaths.ResolveClaudeDir(option, environment);

        // Assert
        resolved.Should().Be(expected == "profile" ? Path.Join(Profile, ".claude") : expected);
    }

    [Fact]
    public void ResolveUserProfile_UserProfileThenHome()
    {
        // Arrange
        var environment = new Dictionary<string, string?> { ["HOME"] = Profile };

        // Act
        string resolved = SetupPaths.ResolveUserProfile(environment);

        // Assert
        resolved.Should().Be(Profile);
    }

    [Fact]
    public void ResolveLocalAppData_VariableThenFolder()
    {
        // Arrange
        var withVariable = new Dictionary<string, string?> { ["LOCALAPPDATA"] = "from-variable" };
        var without = new Dictionary<string, string?>();

        // Act
        (string fromVariable, string fromFolder) = (SetupPaths.ResolveLocalAppData(withVariable), SetupPaths.ResolveLocalAppData(without));

        // Assert
        fromVariable.Should().Be("from-variable");
        fromFolder.Should().Be(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    }

    [Theory]
    [InlineData(true, "C:/agents/zyggy-core")]
    [InlineData(false, "agents")]
    public void DefaultCorePath_WindowsCAgents_ElseProfileAgents(bool windows, string expected)
    {
        // Act
        string path = SetupPaths.DefaultCorePath(windows, Profile);

        // Assert
        path.Should().Be(windows ? expected : Path.Join(Profile, expected, "zyggy-core"));
    }

    [Fact]
    public void Backup_StampSuffix()
    {
        // Act
        (string backup, string temp) = (SetupPaths.Backup("node.json", "20261104T093000Z"), SetupPaths.Temp("node.json", "20261104T093000Z"));

        // Assert
        backup.Should().Be("node.json.bak-20261104T093000Z");
        temp.Should().Be("node.json.tmp-20261104T093000Z");
    }

    [Fact]
    public void Layout_SkillsSettingsPatternsSpikeStore()
    {
        // Act
        string[] paths =
        [
            SetupPaths.SkillSource("core", "remember"), SetupPaths.SkillTarget("claude", "archive"), SetupPaths.SettingsFile("claude"),
            SetupPaths.SkillBackup("local", "20261104T093000Z", "remember"), SetupPaths.SpikeStore("local", SetupTestFiles.Acme), SetupPaths.SecretPatterns("C:/agents/zyggy-core"),
        ];

        // Assert
        paths.Should().Equal(
            Path.Join("core", "node", "skills", "remember"),
            Path.Join("claude", "skills", "archive"),
            Path.Join("claude", "settings.json"),
            Path.Join("local", "zyggy", "backup", "20261104T093000Z", "skills", "remember"),
            Path.Join("local", "zyggy", "secrets", "acme"),
            "C:/agents/zyggy-core/.claude/hooks/secret-patterns.txt");
    }
}
