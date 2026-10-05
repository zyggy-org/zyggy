using System.Text;

using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Memory;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.M365;

/// <summary>
/// <c>zyggy m365 facts</c> against the four <c>facts:</c> cases of the template's <c>m365.bats</c> (spec 33 AC-12, AC-10): same
/// inbox bytes, same counts line, refused text never echoed, exit 0/3/4/5.
/// </summary>
public sealed class FactsVerbTests : IDisposable
{
    private const string Source = "m365-mail 2026-09-30 Invoice 2026-41";
    private const string Usage = " (usage: zyggy m365 facts --kind brief|mail-backfill|files-backfill --source <tag> [--max <n>] < lines)\n";

    private readonly MemoryTree _tree = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero));
    private readonly Dictionary<string, string?> _environment;

    public FactsVerbTests()
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

    public static TheoryData<string[], string> BadArguments() => new()
    {
        { ["--kind", "x", "--source", "s"], "--kind must be brief, mail-backfill or files-backfill" },
        { ["--source", "s"], "--kind is required" },
        { ["--kind", "brief"], "--source is required" },
        { ["--kind", "brief", "--source"], "--source needs a value" },
        { ["--kind", "brief", "--source", "s", "--max", "0"], "--max must be 1..99999" },
        { ["--kind", "brief", "--source", "s", "--max", "abc"], "--max must be 1..99999" },
        { ["--kind", "brief", "--source", "s", "--max", "100000"], "--max must be 1..99999" },
        { ["--kind", "brief", "--source", "s", "extra"], "unexpected argument 'extra'" },
        { ["--kind", "brief", "--kind", "brief", "--source", "s"], "--kind given twice" },
        { ["--kind", "brief", "--source", "s", "--source", "t"], "--source given twice" },
        { ["--kind", "brief", "--source", "m365-mail 2026-09-30\nInjected"], "--source must be one line without control characters" },
        { ["--kind", "brief", "--source", "m365-mail [x]"], "--source must not contain [ or ]" },
        { ["--kind", "brief", "--source", "m365-mail carol@example.org"], "--source holds an e-mail address" },
        { ["--kind", "brief", "--source", "m365-mail https://example.org/x"], "--source holds a URL" },
        { ["--kind", "brief", "--source", "m365-mail AKIAABCDEFGHIJKLMNOP"], "--source matches secret pattern aws-access-key" },
        { ["--kind", "brief", "--source", "   "], "--source is empty" },
        { ["--kind", "brief", "--source", new string('w', 201)], "--source is longer than 200 characters" },
    };

    public void Dispose() => _tree.Dispose();

    [Fact]
    public async Task Run_BriefFixture_ExitZeroNoStdoutFileByteEqualsExpected()
    {
        // Act
        var (exit, console) = await RunAsync(Fixture("facts-brief.txt"), "--kind", "brief", "--source", Source);

        // Assert
        exit.Should().Be(0);
        console.Stdout.Should().BeEmpty();
        console.Stderr.Should().Be(
            "facts: 4 accepted, 6 refused (1 empty, 1 non-letter start, 1 e-mail address, 1 url, 1 phone, 1 secret pattern github-token), 1 duplicate dropped, 1 cut to 240\n");
        console.Stderr.Should().NotContainAny("carol@", "https", "+32", "ghp_", "starts with dash");
        File.ReadAllBytes(_tree.Full("inbox/m365-brief-2026-09-30.md")).Should().Equal(File.ReadAllBytes(M365Run.Golden("expected", "m365-facts-brief.md")));
    }

    [Fact]
    public async Task Run_RefusedFixture_CountsLineNamesReasonsNeverEchoes()
    {
        // Arrange
        const string src = "m365-file ops:/Reports/q3.docx 2026-09-30";

        // Act
        var (exit, console) = await RunAsync(Fixture("facts-refused.txt"), "--kind", "files-backfill", "--source", src);

        // Assert
        exit.Should().Be(0);
        console.Stderr.Should().Be(
            "facts: 1 accepted, 5 refused (1 non-letter start, 1 url, 1 secret pattern iban, 1 secret pattern card-number, 1 secret pattern long-opaque-token), 0 duplicates dropped, 1 cut to 240\n");
        var file = File.ReadAllText(_tree.Full("inbox/m365-files-backfill-2026-09-30.md"));
        file.Should().NotContainAny("BE71", "4111", "www.", "Qm9i");
        file.Should().Contain("name: m365 files-backfill 2026-09-30\n");
        var fact = file.Split('\n').Single(l => l.StartsWith("- [observed] 2026-09-30 [" + src + "]: Example Org renewed", StringComparison.Ordinal));
        TextCollapse.CharCount(fact[fact.IndexOf("]: ", StringComparison.Ordinal)..][3..]).Should().Be(240);
        fact.Should().EndWith("…");
    }

    [Fact]
    public async Task Run_ControlCharactersAndTabs_CleanedLine()
    {
        // Arrange
        const string src = "m365-file ops:/Reports/q3.docx 2026-09-30";

        // Act
        var (exit, console) = await RunAsync("Alice\tleads the\a weekly\r review.\n", "--kind", "files-backfill", "--source", src);

        // Assert
        exit.Should().Be(0);
        console.Stderr.Should().Be("facts: 1 accepted, 0 refused, 0 duplicates dropped, 0 cut to 240\n");
        File.ReadAllLines(_tree.Full("inbox/m365-files-backfill-2026-09-30.md")).Should().Contain($"- [observed] 2026-09-30 [{src}]: Alice leads the weekly review.");
    }

    [Fact]
    public async Task Run_Max3FourGoodLines_ThreeWrittenExitFive_SecondRunAppendsDropsDuplicates()
    {
        // Arrange
        const string src = "m365-mail 2026-09-12 Quarterly planning";
        var input = Fixture("facts-backfill.txt");
        var file = _tree.Full("inbox/m365-mail-backfill-2026-09-30.md");
        var expected = File.ReadAllBytes(M365Run.Golden("expected", "m365-facts-backfill.md"));

        // Act
        var (first, firstConsole) = await RunAsync(input, "--kind", "mail-backfill", "--source", src, "--max", "3");
        var firstLines = File.ReadAllLines(file).Count(l => l.StartsWith("- [observed]", StringComparison.Ordinal));
        var (second, secondConsole) = await RunAsync(input, "--kind", "mail-backfill", "--source", src);
        var afterSecond = File.ReadAllBytes(file);
        var (third, thirdConsole) = await RunAsync(input, "--kind", "mail-backfill", "--source", "m365-mail 2026-09-13 Planning follow-up");

        // Assert
        first.Should().Be(5);
        firstConsole.Stdout.Should().BeEmpty();
        firstConsole.Stderr.Should().Be("facts: 3 accepted, 0 refused, 0 duplicates dropped, 0 cut to 240\nfacts: cap 3 reached\n");
        firstLines.Should().Be(3);
        second.Should().Be(0);
        secondConsole.Stderr.Should().Be("facts: 1 accepted, 0 refused, 3 duplicates dropped, 0 cut to 240\n");
        afterSecond.Should().Equal(expected);
        third.Should().Be(0);
        thirdConsole.Stderr.Should().Be("facts: 0 accepted, 0 refused, 4 duplicates dropped, 0 cut to 240\n");
        File.ReadAllBytes(file).Should().Equal(expected);
    }

    [Theory]
    [MemberData(nameof(BadArguments))]
    public async Task Run_BadArguments_ExitFourWithShellMessage(string[] args, string message)
    {
        // Act
        var (exit, console) = await RunAsync(Fixture("facts-backfill.txt"), args);

        // Assert
        exit.Should().Be(4);
        console.Stdout.Should().BeEmpty();
        console.Stderr.Should().Be("facts: " + message + Usage);
        console.Stderr.Should().NotContainAny("Injected", "carol@", "https", "AKIA");
        Directory.Exists(_tree.Full("inbox")).Should().BeFalse();
    }

    [Fact]
    public async Task Run_EmptyStdin_ZeroAcceptedNoFile()
    {
        // Act
        var (exit, console) = await RunAsync(string.Empty, "--kind", "brief", "--source", Source);

        // Assert
        exit.Should().Be(0);
        console.Stderr.Should().Be("facts: 0 accepted, 0 refused, 0 duplicates dropped, 0 cut to 240\n");
        Directory.Exists(_tree.Full("inbox")).Should().BeFalse();
    }

    [Fact]
    public async Task Run_LastLineWithoutNewline_Read()
    {
        // Act
        var (exit, console) = await RunAsync("Alice owns the plan.\nBob owns the budget.", "--kind", "brief", "--source", Source);

        // Assert
        exit.Should().Be(0);
        console.Stderr.Should().Be("facts: 2 accepted, 0 refused, 0 duplicates dropped, 0 cut to 240\n");
    }

    [Fact]
    public async Task Run_TenantUnset_ExitsThree()
    {
        // Arrange
        _environment.Remove("ZYGGY_TENANT");

        // Act
        var (exit, console) = await RunAsync(Fixture("facts-backfill.txt"), "--kind", "brief", "--source", Source);

        // Assert
        exit.Should().Be(3);
        console.Stderr.Should().Be("facts: configuration error: ZYGGY_TENANT is not set\n");
        Directory.Exists(_tree.Full("inbox")).Should().BeFalse();
    }

    [Fact]
    public async Task Run_HooksOff_Accepted()
    {
        // Arrange
        _environment["ZYGGY_HOOKS"] = "off";

        // Act
        var (exit, console) = await RunAsync(Fixture("facts-backfill.txt"), "--kind", "brief", "--source", Source);

        // Assert
        exit.Should().Be(0);
        console.Stderr.Should().Be("facts: 4 accepted, 0 refused, 0 duplicates dropped, 0 cut to 240\n");
    }

    private static string Fixture(string name) => new UTF8Encoding(false).GetString(File.ReadAllBytes(M365Run.Golden("fixtures", name)));

    private Task<(int Exit, VerbConsole Console)> RunAsync(string stdin, params string[] args) =>
        M365Run.RunAsync(_environment, _clock, ["facts", .. args], stdin);
}
