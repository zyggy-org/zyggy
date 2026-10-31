using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Memory;

namespace Zyggy.Core.Tests.Memory;

/// <summary>Spec 12 A1 / AC-8: the one public way a host registers the secret patterns, failing closed when they cannot be read.</summary>
public sealed class SecretPatternsRegistrationTests : IDisposable
{
    private const string NotSet = "ZYGGY_SECRET_PATTERNS is not set (and no ZYGGY_INSTANCE_DIR or CLAUDE_PROJECT_DIR to derive it from)";

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "zyggy-ut", Guid.NewGuid().ToString("N"));

    private static string Golden => Path.Combine(Infrastructure.Golden.Directory, "secret-patterns", "secret-patterns.txt");

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    [Fact]
    public void AddSecretPatterns_ExplicitPath_RegistersSingletonReturnsNull()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var error = services.AddSecretPatterns(Env(("ZYGGY_SECRET_PATTERNS", Golden)));

        // Assert
        error.Should().BeNull();
        using var provider = services.BuildServiceProvider();
        var patterns = provider.GetRequiredService<SecretPatterns>();
        patterns.Count.Should().BeGreaterThan(10);
        provider.GetRequiredService<SecretPatterns>().Should().BeSameAs(patterns);
    }

    [Fact]
    public void AddSecretPatterns_DerivedFromClaudeProjectDir_WhenExplicitUnset()
    {
        // Arrange: <project>/.claude/hooks/secret-patterns.txt
        var hooks = Path.Combine(_scratch, ".claude", "hooks");
        Directory.CreateDirectory(hooks);
        File.Copy(Golden, Path.Combine(hooks, "secret-patterns.txt"));
        var services = new ServiceCollection();

        // Act
        var error = services.AddSecretPatterns(Env(("CLAUDE_PROJECT_DIR", _scratch)));

        // Assert
        error.Should().BeNull();
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<SecretPatterns>().Count.Should().BeGreaterThan(10);
    }

    [Fact]
    public void AddSecretPatterns_NoVariable_ReturnsTheRememberSentence_NothingRegistered()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var error = services.AddSecretPatterns(Env());

        // Assert
        error.Should().Be(NotSet);
        using var provider = services.BuildServiceProvider();
        provider.GetService<SecretPatterns>().Should().BeNull();
    }

    [Theory]
    [InlineData(false, "{0} is missing")]
    [InlineData(true, "{0}: malformed line 'no-tab-here'")]
    public void AddSecretPatterns_FileMissingOrMalformed_ReturnsLoadError(bool exists, string expected)
    {
        // Arrange
        Directory.CreateDirectory(_scratch);
        var path = Path.Combine(_scratch, "secret-patterns.txt");
        if (exists)
        {
            File.WriteAllText(path, "no-tab-here\n");
        }

        var services = new ServiceCollection();

        // Act
        var error = services.AddSecretPatterns(Env(("ZYGGY_SECRET_PATTERNS", path)));

        // Assert
        error.Should().Be(string.Format(System.Globalization.CultureInfo.InvariantCulture, expected, path));
        using var provider = services.BuildServiceProvider();
        provider.GetService<SecretPatterns>().Should().BeNull();
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
