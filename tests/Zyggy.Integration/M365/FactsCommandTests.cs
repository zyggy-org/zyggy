using System.Text;

using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.M365;

/// <summary>Step 4 (spec 33 AC-10, AC-12): <c>zyggy m365 facts</c> from the built binary, facts on stdin, real file system and clock.</summary>
public sealed class FactsCommandTests : IDisposable
{
    private const string Source = "m365-mail 2026-09-30 Invoice 2026-41";

    private readonly ScratchDirectory _scratch = new();

    public void Dispose() => _scratch.Dispose();

    private string Inbox => Path.Combine(_scratch.Path, "memory", "acme", "alice", "inbox");

    private static string Golden(params string[] parts) => Path.Combine([AppContext.BaseDirectory, "golden", "m365", .. parts]);

    private static string Fixture(string name) => Encoding.UTF8.GetString(File.ReadAllBytes(Golden("fixtures", name)));

    private Dictionary<string, string?> Env()
    {
        var root = Path.Combine(_scratch.Path, "memory");
        Directory.CreateDirectory(Path.Combine(root, "acme", "alice"));
        return new()
        {
            ["ZYGGY_MEMORY_ROOT"] = root,
            ["ZYGGY_TENANT"] = "acme",
            ["ZYGGY_USER"] = "alice",
            ["ZYGGY_TIMEZONE"] = "UTC",
            ["ZYGGY_SECRET_PATTERNS"] = Path.Combine(AppContext.BaseDirectory, "golden", "secret-patterns", "secret-patterns.txt"),
        };
    }

    private Task<ZyggyRun> Facts(Dictionary<string, string?> env, string stdin, params string[] args) =>
        ZyggyCli.RunAsync(["m365", "facts", .. args], env, stdin, _scratch.Path, TestContext.Current.CancellationToken);

    // The oracle was written on 2026-09-30; only the run date positions move to the real date (test side only).
    private static string OracleOn(string oracle, string date) =>
        Encoding.UTF8.GetString(File.ReadAllBytes(Golden("expected", oracle)))
            .Replace(" 2026-09-30\ndescription:", $" {date}\ndescription:", StringComparison.Ordinal)
            .Replace("run on 2026-09-30 (facts.sh)", $"run on {date} (facts.sh)", StringComparison.Ordinal)
            .Replace("updated: 2026-09-30", $"updated: {date}", StringComparison.Ordinal)
            .Replace("- [observed] 2026-09-30 [", $"- [observed] {date} [", StringComparison.Ordinal);

    private string OnlyInboxFile(string kind)
    {
        var file = Directory.EnumerateFiles(Inbox).Should().ContainSingle().Subject;
        Path.GetFileName(file).Should().MatchRegex($"^m365-{kind}-[0-9]{{4}}-[0-9]{{2}}-[0-9]{{2}}\\.md$");
        return file;
    }

    [Fact]
    public async Task Facts_StdinLines_ExitZeroStdoutEmptyStderrCountsLineInboxLines()
    {
        // Act
        var run = await Facts(Env(), Fixture("facts-brief.txt"), "--kind", "brief", "--source", Source);

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        run.Stdout.Should().BeEmpty();
        run.Stderr.Should().Be(
            "facts: 4 accepted, 6 refused (1 empty, 1 non-letter start, 1 e-mail address, 1 url, 1 phone, 1 secret pattern github-token), 1 duplicate dropped, 1 cut to 240\n");
        var file = OnlyInboxFile("brief");
        var date = Path.GetFileNameWithoutExtension(file)["m365-brief-".Length..];
        Encoding.UTF8.GetString(File.ReadAllBytes(file)).Should().Be(OracleOn("m365-facts-brief.md", date));
    }

    [Fact]
    public async Task Facts_MaxReached_ExitFive()
    {
        // Act
        var run = await Facts(Env(), Fixture("facts-backfill.txt"), "--kind", "mail-backfill", "--source", "m365-mail 2026-09-12 Quarterly planning", "--max", "3");

        // Assert
        run.ExitCode.Should().Be(5);
        run.Stderr.Should().Be("facts: 3 accepted, 0 refused, 0 duplicates dropped, 0 cut to 240\nfacts: cap 3 reached\n");
        File.ReadAllLines(OnlyInboxFile("mail-backfill")).Count(l => l.StartsWith("- [observed]", StringComparison.Ordinal)).Should().Be(3);
    }

    [Fact]
    public async Task Facts_BadKind_ExitFour()
    {
        // Act
        var run = await Facts(Env(), Fixture("facts-backfill.txt"), "--kind", "x", "--source", "s");

        // Assert
        run.ExitCode.Should().Be(4);
        run.Stderr.Should().Be(
            "facts: --kind must be brief, mail-backfill or files-backfill (usage: zyggy m365 facts --kind brief|mail-backfill|files-backfill --source <tag> [--max <n>] < lines)\n");
        Directory.Exists(Inbox).Should().BeFalse();
    }

    [Fact]
    public async Task Facts_ExistingShellWrittenFile_FrontMatterKeptDuplicateDropped()
    {
        // Arrange: the first three lines as facts.sh wrote them, today
        var env = Env();
        var date = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var full = OracleOn("m365-facts-backfill.md", date);
        var shellWritten = string.Join('\n', full.Split('\n')[..8]) + "\n";
        Directory.CreateDirectory(Inbox);
        var file = Path.Combine(Inbox, $"m365-mail-backfill-{date}.md");
        File.WriteAllText(file, shellWritten);

        // Act
        var run = await Facts(env, Fixture("facts-backfill.txt"), "--kind", "mail-backfill", "--source", "m365-mail 2026-09-12 Quarterly planning");

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        run.Stderr.Should().Be("facts: 1 accepted, 0 refused, 3 duplicates dropped, 0 cut to 240\n");
        Encoding.UTF8.GetString(File.ReadAllBytes(file)).Should().Be(full);
    }
}
