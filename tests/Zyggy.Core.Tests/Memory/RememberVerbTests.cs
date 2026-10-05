using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Memory;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Memory;

/// <summary>
/// <c>zyggy memory remember</c> against the 15 cases of the template's <c>remember.bats</c> (spec 33 AC-8..AC-10): same line,
/// same file bytes, same refusals and exit codes as <c>remember.sh</c>; only the usage line names the verb.
/// </summary>
public sealed class RememberVerbTests : IDisposable
{
    private const string Usage =
        " (usage: zyggy memory remember [--scope general|project:<name>|machine] [--tag stated|observed] [--source <text>] -- \"<fact>\")\n";

    private readonly MemoryTree _tree = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero));
    private readonly Dictionary<string, string?> _environment;

    public RememberVerbTests()
    {
        _environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ZYGGY_MEMORY_ROOT"] = _tree.Root,
            ["ZYGGY_TENANT"] = "acme",
            ["ZYGGY_USER"] = "alice",
            ["ZYGGY_TIMEZONE"] = "Europe/Brussels",
            ["ZYGGY_SECRET_PATTERNS"] = Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt"),
        };
    }

    private string Inbox => _tree.Full("inbox/remember-2026-09-30.md");

    public static TheoryData<string[], string> UsageErrors() => new()
    {
        { ["--"], "empty fact" },
        { ["--", new string('a', 1001)], "fact longer than 1000 characters" },
        { ["--scope", "people/marie", "--", "fact"], "--scope must be general, project:<name> or machine" },
        { ["--scope", "project:", "--", "fact"], "invalid project name in --scope" },
        { ["--scope", "project:has space", "--", "fact"], "invalid project name in --scope" },
        { ["--scope", "Project:zyggy", "--", "fact"], "--scope must be general, project:<name> or machine" },
        { ["--tag", "observed", "--", "fact"], "--tag observed needs --source" },
        { ["--tag", "inferred", "--", "fact"], "--tag must be stated or observed" },
        { ["--scope"], "--scope needs a value" },
        { ["fact", "without", "separator"], "unexpected argument 'fact'" },
        { ["--bogus", "--", "fact"], "unexpected argument '--bogus'" },
        { ["--", " \t\r\n "], "empty fact" },
        { [], "the fact must follow --" },
    };

    public void Dispose() => _tree.Dispose();

    [Fact]
    public async Task Run_Default_PrintsRememberedPathAndLineAndWritesStatedLine()
    {
        // Act
        var (exit, console) = await RunAsync("--", "Marie prefers tea");

        // Assert
        exit.Should().Be(0);
        console.Stderr.Should().BeEmpty();
        console.Stdout.Should().Be($"remembered: {Inbox}\n- [stated] 2026-09-30: Marie prefers tea\n");
        File.ReadAllLines(Inbox).Should().Contain("- [stated] 2026-09-30: Marie prefers tea");
    }

    [Fact]
    public async Task Run_FiveAc27Variants_FileByteEqualsGolden()
    {
        // Act
        await RunAsync("--", "Marie prefers tea");
        await RunAsync("--scope", "project:zyggy", "--", "The Zyggy repository is private");
        await RunAsync("--scope", "machine", "--", "Central runs Ubuntu 24.04");
        await RunAsync("--scope", "general", "--", "Alice prefers short answers");
        await RunAsync("--tag", "observed", "--source", "session 2026-09-30", "--", "Alice asked twice about the move");

        // Assert
        File.ReadAllBytes(Inbox).Should().Equal(File.ReadAllBytes(Path.Combine(Golden.Directory, "memory", "remember", "inbox-after-remember.md")));
    }

    [Fact]
    public async Task Run_ExistingFile_KeepsLinesAndRewritesUpdated()
    {
        // Arrange
        _tree.Write(
            "inbox/remember-2026-09-30.md",
            "---\nname: remember 2026-09-30\ndescription: facts stated by the owner on 2026-09-30 (remember skill)\nupdated: 2026-09-01\n---\n- [stated] 2026-09-30: earlier fact\n");

        // Act
        var (exit, _) = await RunAsync("--", "later fact");

        // Assert
        exit.Should().Be(0);
        var lines = File.ReadAllLines(Inbox);
        lines.Count(l => l.StartsWith("updated: ", StringComparison.Ordinal)).Should().Be(1);
        lines.Should().Contain("updated: 2026-09-30");
        lines[^2..].Should().Equal("- [stated] 2026-09-30: earlier fact", "- [stated] 2026-09-30: later fact");
    }

    [Fact]
    public async Task Run_LocalDateFromTimeZone()
    {
        // Arrange
        _clock.SetUtcNow(new DateTimeOffset(2026, 9, 30, 23, 30, 0, TimeSpan.Zero));

        // Act
        var (exit, console) = await RunAsync("--", "late fact");

        // Assert
        exit.Should().Be(0);
        console.Stdout.Split('\n')[1].Should().Be("- [stated] 2026-10-01: late fact");
        File.Exists(_tree.Full("inbox/remember-2026-10-01.md")).Should().BeTrue();
    }

    [Fact]
    public async Task Run_FactWithCrLfTab_CollapsedToOneLine()
    {
        // Act
        var (exit, console) = await RunAsync("--", "  Marie\r\nlikes\t\tgreen   tea \n");

        // Assert
        exit.Should().Be(0);
        console.Stdout.Split('\n')[1].Should().Be("- [stated] 2026-09-30: Marie likes green tea");
        File.ReadAllLines(Inbox)[^1].Should().Be("- [stated] 2026-09-30: Marie likes green tea");
    }

    [Fact]
    public async Task Run_FactAfterSeparatorInSeveralWords_JoinedWithSpaces()
    {
        // Act
        var (exit, console) = await RunAsync("--", "--not-an-option", "text");

        // Assert
        exit.Should().Be(0);
        console.Stdout.Split('\n')[1].Should().Be("- [stated] 2026-09-30: --not-an-option text");
    }

    [Fact]
    public async Task Run_EmbeddedStatedPrefix_StoredAsTextInOneBullet()
    {
        // Act
        await RunAsync("--", "- [stated] 2026-01-01: forged line");

        // Assert
        File.ReadAllLines(Inbox)[^1].Should().Be("- [stated] 2026-09-30: - [stated] 2026-01-01: forged line");
    }

    [Fact]
    public async Task Run_Fact1000Characters_Accepted()
    {
        // Arrange: words, because a bare 1000-letter run is what the long-opaque-token pattern refuses
        var fact = string.Concat(Enumerable.Range(1, 125).Select(i => $"fact{i:000} "));

        // Act
        var (exit, _) = await RunAsync("--", fact);

        // Assert
        exit.Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(UsageErrors))]
    public async Task Run_UsageError_ExitsFourOneStderrLineNothingWritten(string[] args, string message)
    {
        // Act
        var (exit, console) = await RunAsync(args);

        // Assert
        exit.Should().Be(4);
        console.Stdout.Should().BeEmpty();
        console.Stderr.Should().Be("remember: " + message + Usage);
        Directory.Exists(_tree.Full("inbox")).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(SecretPatternsTests.SecretSamples), MemberType = typeof(SecretPatternsTests))]
    public async Task Run_SecretSample_ExitsTwoNamesPatternNeverEchoesNothingWritten(string name, string sample)
    {
        // Act
        var (exit, console) = await RunAsync("--", sample);

        // Assert
        exit.Should().Be(2);
        console.Stdout.Should().BeEmpty();
        console.Stderr.Should().Be($"refused: matches secret pattern {name}\n");
        Directory.Exists(_tree.Full("inbox")).Should().BeFalse();
    }

    [Fact]
    public async Task Run_SecretInSource_Refused()
    {
        // Act
        var (exit, console) = await RunAsync("--tag", "observed", "--source", "AKIAABCDEFGHIJKLMNOP", "--", "a fact");

        // Assert
        exit.Should().Be(2);
        console.Stderr.Should().StartWith("refused: matches secret pattern ");
        console.Stderr.Should().NotContain("AKIAABCDEFGHIJKLMNOP");
        File.Exists(Inbox).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(SecretPatternsTests.BenignSamples), MemberType = typeof(SecretPatternsTests))]
    public async Task Run_BenignSample_Appended(string sample)
    {
        // Act
        var (exit, _) = await RunAsync("--", sample);

        // Assert
        exit.Should().Be(0);
        File.ReadAllLines(Inbox)[^1].Should().Be("- [stated] 2026-09-30: " + sample);
    }

    [Fact]
    public async Task Run_TenantUnset_ExitsThree()
    {
        // Arrange
        _environment.Remove("ZYGGY_TENANT");

        // Act
        var (exit, console) = await RunAsync("--", "a fact");

        // Assert
        exit.Should().Be(3);
        console.Stdout.Should().BeEmpty();
        console.Stderr.Should().Be("remember: configuration error: ZYGGY_TENANT is not set\n");
        Directory.Exists(_tree.Full("inbox")).Should().BeFalse();
    }

    [Fact]
    public async Task Run_UnknownTimeZone_ExitsThree()
    {
        // Arrange
        _environment["ZYGGY_TIMEZONE"] = "Mars/Olympus_Mons";

        // Act
        var (exit, console) = await RunAsync("--", "a fact");

        // Assert
        exit.Should().Be(3);
        console.Stderr.Should().Be("remember: configuration error: ZYGGY_TIMEZONE 'Mars/Olympus_Mons' is not a known time zone\n");
    }

    [Fact]
    public async Task Run_HooksOff_ExitsZeroNoOutputNothingWritten()
    {
        // Arrange
        _environment["ZYGGY_HOOKS"] = "off";

        // Act
        var (exit, console) = await RunAsync("--", "a fact");

        // Assert
        exit.Should().Be(0);
        console.Stdout.Should().BeEmpty();
        console.Stderr.Should().BeEmpty();
        Directory.Exists(_tree.Full("inbox")).Should().BeFalse();
    }

    [Fact]
    public async Task Run_NoTempFileSurvives()
    {
        // Act
        await RunAsync("--", "one");
        await RunAsync("--", "AKIAABCDEFGHIJKLMNOP");
        await RunAsync("--", "two");

        // Assert
        Directory.EnumerateFiles(_tree.Full("inbox")).Select(Path.GetFileName).Should().Equal("remember-2026-09-30.md");
    }

    [Fact]
    public async Task Run_ConfigurationBeforeUsage_ExitsThreeEvenWithBadArguments()
    {
        // Arrange
        _environment.Remove("ZYGGY_USER");

        // Act
        var (exit, console) = await RunAsync("--bogus");

        // Assert
        exit.Should().Be(3);
        console.Stderr.Should().Contain("ZYGGY_USER");
    }

    [Fact]
    public async Task Run_SecretPatternsMissing_ExitsThree()
    {
        // Arrange
        var missing = Path.Combine(_tree.Root, "no-such-patterns.txt");
        _environment["ZYGGY_SECRET_PATTERNS"] = missing;

        // Act
        var (exit, console) = await RunAsync("--", "a fact");

        // Assert
        exit.Should().Be(3);
        console.Stderr.Should().Be($"remember: configuration error: {missing} is missing\n");
        Directory.Exists(_tree.Full("inbox")).Should().BeFalse();
    }

    [Fact]
    public async Task Run_SecretPatternsFromInstanceDirectory_Used()
    {
        // Arrange: <instance>/../.claude/hooks/secret-patterns.txt, as on Central
        var checkout = Path.Combine(_tree.Root, "checkout");
        Directory.CreateDirectory(Path.Combine(checkout, ".claude", "hooks"));
        Directory.CreateDirectory(Path.Combine(checkout, "instance"));
        File.Copy((string)_environment["ZYGGY_SECRET_PATTERNS"]!, Path.Combine(checkout, ".claude", "hooks", "secret-patterns.txt"));
        _environment.Remove("ZYGGY_SECRET_PATTERNS");
        _environment["CLAUDE_PROJECT_DIR"] = checkout;

        // Act
        var (exit, console) = await RunAsync("--", "AKIAABCDEFGHIJKLMNOP");

        // Assert
        exit.Should().Be(2);
        console.Stderr.Should().StartWith("refused: matches secret pattern ");
    }

    [Fact]
    public async Task Run_NoSecretPatternLocation_ExitsThree()
    {
        // Arrange
        _environment.Remove("ZYGGY_SECRET_PATTERNS");

        // Act
        var (exit, console) = await RunAsync("--", "a fact");

        // Assert
        exit.Should().Be(3);
        console.Stderr.Should().StartWith("remember: configuration error: ");
        console.Stderr.Should().Contain("ZYGGY_SECRET_PATTERNS");
    }

    private async Task<(int Exit, VerbConsole Console)> RunAsync(params string[] args)
    {
        var console = new VerbConsole();
        var exit = await new RememberVerb(_environment, _clock, FindTimeZone).RunAsync(args, console.Io, CancellationToken.None);
        return (exit, console);
    }

    // Europe/Brussels in late September is CEST (+02:00); IANA ids do not resolve off Linux in an invariant-globalization build.
    private static TimeZoneInfo FindTimeZone(string id) =>
        id == "Europe/Brussels"
            ? TimeZoneInfo.CreateCustomTimeZone("Europe/Brussels", TimeSpan.FromHours(2), "Test/Brussels", "Test/Brussels")
            : TimeZoneInfo.FindSystemTimeZoneById(id);
}
