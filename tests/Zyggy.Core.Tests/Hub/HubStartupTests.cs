using Zyggy.Core.Hub;
using Zyggy.Core.Memory;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Hub;

public sealed class HubStartupTests : IDisposable
{
    private static readonly string GoldenPatterns = Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt");

    private readonly MemoryTree _tree = new();
    private readonly Dictionary<string, string?> _environment;

    public HubStartupTests() => _environment = new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        ["ZYGGY_MEMORY_ROOT"] = _tree.Root,
        ["ZYGGY_TENANT"] = "acme",
        ["ZYGGY_USER"] = "alice",
        ["ZYGGY_TIMEZONE"] = "Europe/Brussels",
        ["ZYGGY_SECRET_PATTERNS"] = GoldenPatterns,
    };

    public void Dispose() => _tree.Dispose();

    [Fact]
    public void Decide_EnvironmentComplete_StartsWithPathsZonePatterns_HooksOffFalse()
    {
        // Act
        var start = Decide();

        // Assert
        start.Exit.Should().BeNull();
        start.Message.Should().BeNull();
        start.Paths!.Principal.Should().Be(MemoryTree.Alice);
        start.Paths.PrincipalDirectory.Should().Be(_tree.Paths.PrincipalDirectory);
        start.TimeZone.Should().BeSameAs(HubHarness.Brussels);
        start.Patterns!.Count.Should().BePositive();
        start.HooksOff.Should().BeFalse();
    }

    [Fact]
    public void Decide_TenantUnset_ExitThreeMemoryEnvironmentMessage()
    {
        // Arrange
        _environment.Remove("ZYGGY_TENANT");

        // Act
        var start = Decide();

        // Assert
        start.Exit.Should().Be(3);
        start.Message.Should().Be("hub: configuration error: ZYGGY_TENANT is not set");
        start.Paths.Should().BeNull();
    }

    [Fact]
    public void Decide_PrincipalDirectoryMissing_ExitThree()
    {
        // Arrange
        _environment["ZYGGY_USER"] = "bob";
        var missing = new MemoryPaths(_tree.Root, new Principal(TenantId.Parse("acme"), UserId.Parse("bob"))).PrincipalDirectory;

        // Act
        var start = Decide();

        // Assert
        start.Exit.Should().Be(3);
        start.Message.Should().Be($"hub: configuration error: memory directory {missing} does not exist (ZYGGY_TENANT/ZYGGY_USER)");
    }

    [Fact]
    public void Decide_UnknownTimeZone_ExitThree()
    {
        // Arrange
        _environment["ZYGGY_TIMEZONE"] = "Mars/Olympus_Mons";

        // Act
        var start = Decide();

        // Assert
        start.Exit.Should().Be(3);
        start.Message.Should().Be("hub: configuration error: ZYGGY_TIMEZONE 'Mars/Olympus_Mons' is not a known time zone");
    }

    [Fact]
    public void Decide_PatternsMissing_ExitThreeNamesPath()
    {
        // Arrange
        var missing = Path.Combine(_tree.Root, "no-such-patterns.txt");
        _environment["ZYGGY_SECRET_PATTERNS"] = missing;

        // Act
        var start = Decide();

        // Assert
        start.Exit.Should().Be(3);
        start.Message.Should().Be($"hub: configuration error: {missing} is missing");
    }

    [Fact]
    public void Decide_PatternsMalformed_ExitThreeNamesLine()
    {
        // Arrange — a pattern line without a tab.
        var malformed = Path.Combine(_tree.Root, "patterns.txt");
        File.WriteAllText(malformed, "no-tab-here\n");
        _environment["ZYGGY_SECRET_PATTERNS"] = malformed;

        // Act
        var start = Decide();

        // Assert
        start.Exit.Should().Be(3);
        start.Message.Should().Be($"hub: configuration error: {malformed}: malformed line 'no-tab-here'");
    }

    [Fact]
    public void Decide_NoPatternLocation_ExitThreeNamesVariable()
    {
        // Arrange
        _environment.Remove("ZYGGY_SECRET_PATTERNS");

        // Act
        var start = Decide();

        // Assert
        start.Exit.Should().Be(3);
        start.Message.Should().StartWith("hub: configuration error: ").And.Contain("ZYGGY_SECRET_PATTERNS");
    }

    [Fact]
    public void Decide_HooksOff_StartsWithHooksOffTrue()
    {
        // Arrange
        _environment["ZYGGY_HOOKS"] = "off";

        // Act
        var start = Decide();

        // Assert
        start.Exit.Should().BeNull();
        start.HooksOff.Should().BeTrue();
    }

    [Fact]
    public void Decide_UnsetVariablesFilledFromSettingsLocalJson_SetOnesWin()
    {
        // Arrange — the checkout's settings name acme/alice; the process environment sets only ZYGGY_USER=bob.
        var checkout = Checkout($$$"""{"env":{"ZYGGY_MEMORY_ROOT":{{{Json(_tree.Root)}}},"ZYGGY_TENANT":"acme","ZYGGY_USER":"alice","ZYGGY_TIMEZONE":"Europe/Brussels"}}""");
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal) { ["CLAUDE_PROJECT_DIR"] = checkout, ["ZYGGY_USER"] = "bob" };

        // Act
        var withBob = HubStartup.Decide(environment, FindTimeZone);
        environment.Remove("ZYGGY_USER");
        var fromSettings = HubStartup.Decide(environment, FindTimeZone);

        // Assert — bob has no directory, so the set variable is what the refusal names; without it the settings give acme/alice.
        withBob.Exit.Should().Be(3);
        withBob.Message.Should().Contain($"{Path.DirectorySeparatorChar}bob ");
        fromSettings.Exit.Should().BeNull();
        fromSettings.Paths!.Principal.Should().Be(MemoryTree.Alice);
        fromSettings.TimeZone.Should().BeSameAs(HubHarness.Brussels);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("""{"env":"x"}""")]
    [InlineData(null)]
    public void Decide_SettingsFileUnreadableOrNotJson_ChangesNothing(string? content)
    {
        // Arrange — the environment is complete; a broken or absent settings file must not change it.
        var checkout = Checkout(content);
        _environment["CLAUDE_PROJECT_DIR"] = checkout;

        // Act
        var start = Decide();

        // Assert
        start.Exit.Should().BeNull();
        start.Paths!.Principal.Should().Be(MemoryTree.Alice);
    }

    [Fact]
    public void Decide_ClaudeProjectDirUnsetAndEnvironmentComplete_FallbackSkipped()
    {
        // Act
        var start = Decide();

        // Assert
        _environment.Should().NotContainKey("CLAUDE_PROJECT_DIR");
        start.Exit.Should().BeNull();
    }

    [Fact]
    public void Decide_PatternsDerivedFromClaudeProjectDir_WhenExplicitUnset()
    {
        // Arrange — <CLAUDE_PROJECT_DIR>/instance/../.claude/hooks/secret-patterns.txt
        var checkout = Checkout(null);
        _environment.Remove("ZYGGY_SECRET_PATTERNS");
        _environment["CLAUDE_PROJECT_DIR"] = checkout;

        // Act
        var start = Decide();

        // Assert
        start.Exit.Should().BeNull();
        start.Patterns!.Count.Should().Be(HubHarness.Patterns.Count);
    }

    private static TimeZoneInfo FindTimeZone(string id) =>
        id == "Europe/Brussels" ? HubHarness.Brussels : TimeZoneInfo.FindSystemTimeZoneById(id);

    private static string Json(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    private HubStart Decide() => HubStartup.Decide(_environment, FindTimeZone);

    // A scratch checkout: .claude/hooks/secret-patterns.txt from the golden, and .claude/settings.local.json when content is given.
    private string Checkout(string? settings)
    {
        var checkout = Path.Combine(_tree.Root, "checkout");
        Directory.CreateDirectory(Path.Combine(checkout, ".claude", "hooks"));
        File.Copy(GoldenPatterns, Path.Combine(checkout, ".claude", "hooks", "secret-patterns.txt"), overwrite: true);
        if (settings is not null)
        {
            File.WriteAllText(Path.Combine(checkout, ".claude", "settings.local.json"), settings);
        }

        return checkout;
    }
}
