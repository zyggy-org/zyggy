using System.Globalization;
using System.Text;
using System.Text.Json;

using Zyggy.Core.Dream;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Dream;

/// <summary>AC-22, AC-19, AC-10, AC-18, AC-13 (integration): one real run against a bare remote and fake-claude.</summary>
public sealed class DreamEndToEndTests : IAsyncLifetime
{
    private const string L1 = "- [stated] 2026-09-29: Carol likes green tea.";
    private const string L4 = "- [observed] 2026-09-29 [m365-mail 2026-09-29]: Acme Corp renewed its support contract until 2027.";

    private readonly MemoryRepoFixture _repo = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Today => DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public async ValueTask InitializeAsync()
    {
        await _repo.InitializeAsync();
        await _repo.SeedAsync("dream", Ct);
    }

    public ValueTask DisposeAsync() => _repo.DisposeAsync();

    [Fact]
    public async Task Dream_HappyPath_FilesIntoExistingAndNewCategoryOnBothSidesAndPushesOneCommit()
    {
        // Arrange
        var before = await _repo.CommitCountAsync(Ct);

        // Act
        var run = await _repo.DreamAsync("dream-file-ok", Ct);

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr + run.Stdout);
        (await _repo.CommitCountAsync(Ct)).Should().Be(before + 1);
        (await _repo.LastCommitSubjectAsync(Ct)).Should().BeOneOf($"dream {Today}", $"dream {DateTime.UtcNow.AddMinutes(-5):yyyy-MM-dd}");
        (await _repo.ShowAsync("acme/alice/business/clients/_index.md", Ct)).Should().Contain("name: clients").And.Contain("description: Companies Alice works for");
        (await _repo.ShowAsync("acme/alice/business/clients/acme-corp.md", Ct)).Should().Contain(L4);
        (await _repo.ShowAsync("acme/alice/private/topics/running.md", Ct)).Should().Contain("I run every Sunday morning.");
        (await _repo.ShowAsync("acme/alice/private/people/carol.md", Ct)).Should().Contain(L1);
        (await _repo.ShowAsync("acme/alice/business/areas/zyggy.md", Ct)).Should().Contain("Repository zyggy is private on GitHub.");
        var body = await _repo.LastCommitBodyAsync(Ct);
        body.Should().StartWith("outcome: committed").And.Contain("Zyggy-Run: ").And.Contain("Zyggy-Trigger: manual");
    }

    [Fact]
    public async Task Dream_HappyPath_LedgerInSameCommitListsEveryOfferedLine()
    {
        // Act
        var run = await _repo.DreamAsync("dream-file-ok", Ct);

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        (await _repo.LastCommitPathsAsync(Ct)).Should().Contain("acme/alice/.dream/ledger.json").And.Contain("acme/alice/private/people/carol.md");
        using var ledger = JsonDocument.Parse(await _repo.ShowAsync("acme/alice/.dream/ledger.json", Ct));
        var files = ledger.RootElement.GetProperty("files");
        files.GetProperty("inbox/remember-2026-09-29.md").GetProperty("consumed").GetArrayLength().Should().Be(2);
        files.GetProperty("inbox/github-inventory-2026-09-29.md").GetProperty("consumed").GetArrayLength().Should().Be(1);
        files.GetProperty("inbox/m365-mail-backfill-2026-09-29.md").GetProperty("consumed").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Dream_HappyPath_InboxNeverCommittedAndUnchangedOnDisk()
    {
        // Arrange
        var inbox = Path.Combine(_repo.PrincipalDir, "inbox");
        var before = Directory.EnumerateFiles(inbox).ToDictionary(f => f, f => File.ReadAllText(f));

        // Act
        var run = await _repo.DreamAsync("dream-file-ok", Ct);

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        (await _repo.LastCommitPathsAsync(Ct)).Should().NotContain(p => p.Contains("/inbox/", StringComparison.Ordinal));
        Directory.EnumerateFiles(inbox).ToDictionary(f => f, f => File.ReadAllText(f)).Should().Equal(before);
        var tracked = await _repo.GitAsync(_repo.CloneDir, ["ls-files", "acme/alice/inbox"], Ct);
        tracked.Should().BeEmpty();
    }

    [Fact]
    public async Task Dream_HappyPath_OtherStagedChangeStaysStagedAndOutOfCommit()
    {
        // Arrange
        var profile = Path.Combine(_repo.PrincipalDir, "profile.md");
        await File.AppendAllTextAsync(profile, "- [stated] 2026-10-01: staged by someone else.\n", Ct);
        await _repo.GitAsync(_repo.CloneDir, ["add", "acme/alice/profile.md"], Ct);

        // Act
        var run = await _repo.DreamAsync("dream-file-ok", Ct);

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        (await _repo.LastCommitPathsAsync(Ct)).Should().NotContain("acme/alice/profile.md");
        (await _repo.StatusAsync(Ct)).Should().Contain("M  acme/alice/profile.md");
    }

    [Fact]
    public async Task Dream_HappyPath_CapturedArgumentsWorkingDirectoryAndStdinMatchContract()
    {
        // Arrange
        var argsPath = Path.Combine(_repo.RootDir, "args.bin");
        var stdinPath = Path.Combine(_repo.RootDir, "stdin.bin");
        var prompts = new DreamPrompts();

        // Act
        var run = await _repo.DreamAsync("dream-file-ok", Ct, new Dictionary<string, string?>
        {
            ["ZYGGY_FAKE_CLAUDE_CAPTURE"] = argsPath,
            ["ZYGGY_FAKE_CLAUDE_STDIN_CAPTURE"] = stdinPath,
        });

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        var capture = FakeClaude.ReadCapture(argsPath);
        string[] expected =
        [
            "-p", "--output-format", "stream-json", "--verbose", "--permission-mode", "auto", "--permission-prompts", "none",
            "--no-session-persistence", "--max-turns", "30", "--max-budget-usd", "5", "--tools", "Read,Grep,Glob",
            "--disallowedTools", $"mcp__*,mcp__linkedin__*,Bash(zyggy linkedin *),Read(//{_repo.PrincipalDir.Replace('\\', '/').TrimStart('/')}/archive/**)",
            "--add-dir", _repo.PrincipalDir, "--json-schema", prompts.FilingSchema, "--append-system-prompt", prompts.FilingPrompt,
            "--strict-mcp-config", "--settings", """{"disableAllHooks":true,"autoMemoryEnabled":false}""",
            "--disable-slash-commands",
        ];
        capture.Arguments.Should().Equal(expected);
        Path.GetDirectoryName(Path.GetFullPath(capture.WorkingDirectory)).Should().Be(Path.Combine(_repo.StateDir, "runs"));
        Directory.Exists(capture.WorkingDirectory).Should().BeFalse("the run directory is deleted after the call");
        var stdin = Encoding.UTF8.GetString(File.ReadAllBytes(stdinPath));
        stdin.Should().Contain("<<<").And.Contain(">>>");
        foreach (var id in new[] { "L1 ", "L2 ", "L3 ", "L4 ", "L5 " })
        {
            stdin.Should().Contain("\n" + id);
        }
    }

    [Fact]
    public async Task Dream_HappyPath_RunRecordCompleteWithCostTurnsAndNoFactText()
    {
        // Act
        var run = await _repo.DreamAsync("dream-file-ok", Ct);

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        var record = DreamRunRecordStore.ReadLast(_repo.StateDir)!;
        record.Outcome.Should().Be("committed");
        record.Pushed.Should().BeTrue();
        record.Commit.Should().Be(await _repo.GitAsync(_repo.BareDir, ["rev-parse", "main"], Ct));
        record.CostUsdTotal.Should().Be(0.0421m);
        record.Batches.Should().ContainSingle().Which.Should().Match<DreamBatchRecord>(b =>
            b.Lines == 5 && b.Filed == 5 && b.FilesCreated == 2 && b.FilesEdited == 2 && b.CategoriesCreated == 1 && b.Turns == 3);
        record.InboxRemaining.Should().Be(new DreamInboxRemaining(0, 0));
        var json = await File.ReadAllTextAsync(Path.Combine(_repo.StateDir, "dream-runs.jsonl"), Ct);
        json.Should().NotContain("green tea").And.NotContain("Acme Corp").And.NotContain("Sunday");
    }
}
