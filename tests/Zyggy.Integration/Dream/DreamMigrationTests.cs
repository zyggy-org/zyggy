using Zyggy.Core.Dream;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Dream;

/// <summary>AC-27 (integration): the first run on the 27 layout migrates it in one pushed commit, byte for byte.</summary>
public sealed class DreamMigrationTests : IAsyncLifetime
{
    private static readonly (string From, string To)[] Moves =
    [
        ("areas/house-move.md", "private/areas/house-move.md"),
        ("areas/marathon.md", "private/areas/marathon.md"),
        ("areas/zyggy.md", "business/areas/zyggy.md"),
        ("people/bob.md", "business/people/bob.md"),
        ("people/carol.md", "private/people/carol.md"),
        ("topics/tea.md", "private/topics/tea.md"),
        ("topics/tools.md", "business/topics/tools.md"),
    ];

    private readonly MemoryRepoFixture _repo = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _repo.InitializeAsync();
        await _repo.SeedAsync("dream-legacy", Ct);
    }

    public ValueTask DisposeAsync() => _repo.DisposeAsync();

    private Task<string> Blob(string path) => _repo.GitAsync(_repo.BareDir, ["rev-parse", $"main:acme/alice/{path}"], Ct);

    [Fact]
    public async Task Dream_LegacyLayout_OneCommitLayoutMigratedEveryFileMovedByteIdentical()
    {
        // Arrange
        var before = new Dictionary<string, string>();
        foreach (var (from, _) in Moves)
        {
            before[from] = await Blob(from);
        }

        var commits = await _repo.CommitCountAsync(Ct);

        // Act
        var run = await _repo.DreamAsync("dream-migrate-ok", Ct);

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr + run.Stdout);
        (await _repo.CommitCountAsync(Ct)).Should().Be(commits + 1);
        (await _repo.LastCommitBodyAsync(Ct)).Should().StartWith("layout migrated");
        foreach (var (from, to) in Moves)
        {
            (await Blob(to)).Should().Be(before[from], $"{from} moves to {to} unchanged");
        }

        DreamRunRecordStore.ReadLast(_repo.StateDir)!.Migrated.Should().Be(7);
    }

    [Fact]
    public async Task Dream_LegacyLayout_LegacyDirectoriesGoneIndexesPresent()
    {
        // Act
        var run = await _repo.DreamAsync("dream-migrate-ok", Ct);

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        (await _repo.GitAsync(_repo.BareDir, ["ls-tree", "--name-only", "main", "acme/alice/"], Ct)).Split('\n')
            .Should().NotContain(["acme/alice/areas", "acme/alice/people", "acme/alice/topics"]);
        foreach (var side in new[] { "private", "business" })
        {
            foreach (var category in new[] { "areas", "people", "topics" })
            {
                (await _repo.ShowAsync($"acme/alice/{side}/{category}/_index.md", Ct)).Should().Contain($"name: {category}");
            }
        }

        foreach (var legacy in new[] { "areas", "people", "topics" })
        {
            Directory.Exists(Path.Combine(_repo.PrincipalDir, legacy)).Should().BeFalse();
        }
    }

    [Fact]
    public async Task Dream_LegacyLayout_InboxNotOfferedInMigrationRun()
    {
        // Arrange
        var stdin = Path.Combine(_repo.RootDir, "stdin.txt");

        // Act
        var run = await _repo.DreamAsync("dream-migrate-ok", Ct, new Dictionary<string, string?> { ["ZYGGY_FAKE_CLAUDE_STDIN_CAPTURE"] = stdin });

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        var offered = await File.ReadAllTextAsync(stdin, Ct);
        offered.Should().Contain("Legacy files (data)").And.NotContain("Carol likes green tea.");
        var record = DreamRunRecordStore.ReadLast(_repo.StateDir)!;
        record.Batches.Should().BeEmpty();
        record.InboxRemaining.Lines.Should().Be(0, "a migration run does not count the backlog");
        File.Exists(Path.Combine(_repo.PrincipalDir, ".dream", "ledger.json")).Should().BeFalse();
    }

    [Fact]
    public async Task Dream_BadMigration_ExitFiveMigrationRejectedTreeUnchanged()
    {
        // Arrange
        var head = await _repo.GitAsync(_repo.BareDir, ["rev-parse", "main"], Ct);
        var tree = Directory.EnumerateFiles(_repo.PrincipalDir, "*", SearchOption.AllDirectories).Order().ToList();

        // Act
        var run = await _repo.DreamAsync("dream-migrate-bad", Ct);

        // Assert
        run.ExitCode.Should().Be(5, run.Stderr + run.Stdout);
        DreamRunRecordStore.ReadLast(_repo.StateDir)!.Check.Should().Be("migration_rejected");
        (await _repo.GitAsync(_repo.BareDir, ["rev-parse", "main"], Ct)).Should().Be(head);
        Directory.EnumerateFiles(_repo.PrincipalDir, "*", SearchOption.AllDirectories).Order().Should().Equal(tree);
    }

    [Fact]
    public async Task Dream_AfterMigration_DigestIndexListsSidedCategories()
    {
        // Arrange
        (await _repo.DreamAsync("dream-migrate-ok", Ct)).ExitCode.Should().Be(0);

        // Act
        var digest = await ZyggyCli.RunAsync(["memory", "digest", "index"], _repo.DreamEnv("none"), null, _repo.RootDir, Ct);

        // Assert
        digest.ExitCode.Should().Be(0, digest.Stderr);
        digest.Stdout.Should().Contain("- private/areas/ — ").And.Contain("- business/people/ — ").And.Contain("- private/people/carol.md — Carol, Alice's sister");
    }
}
