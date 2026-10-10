using System.Text;

using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Memory;

/// <summary>
/// Plan 37 Step 7 (AC-19 integration): <c>zyggy memory archive remove</c> from the built binary — both files gone on <c>origin/main</c> in one
/// <c>archive remove</c> commit, one inbox line not committed, the project file untouched; <c>not_found</c>, <c>unattended</c>, usage, and a
/// deferred push retried first by the next <c>remove</c>.
/// </summary>
public sealed class ArchiveRemoveCommandTests : IAsyncLifetime
{
    private const string Item = "acme/alice/archive/zyggy/quote-2026.txt";

    private const string Sidecar = "acme/alice/archive/zyggy/quote-2026.md";

    private readonly MemoryRepoFixture _repo = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _repo.InitializeAsync();
        await _repo.SeedAsync("archive", Ct);
    }

    public ValueTask DisposeAsync() => _repo.DisposeAsync();

    private async Task AddAsync(string fileName = "quote.txt", string name = "Quote 2026")
    {
        var source = await _repo.WriteSourceAsync(fileName, Encoding.UTF8.GetBytes($"{name}: the roof repair.\n"));
        var run = await _repo.ArchiveAsync(Ct, null, ["add", "--project", "zyggy", "--name", name, "--description", "Roof repair quote", "--file", source]);
        run.ExitCode.Should().Be(0, run.Stderr);
    }

    private Task<ZyggyRun> RemoveAsync(string reference = "zyggy/quote-2026", IReadOnlyDictionary<string, string?>? env = null) =>
        _repo.ArchiveAsync(Ct, env, ["remove", reference]);

    [Fact]
    public async Task Remove_Existing_BothPathsGoneOnMainOneCommitWithTrailer()
    {
        // Arrange
        await AddAsync();
        var commits = await _repo.CommitCountAsync(Ct);

        // Act
        var run = await RemoveAsync();

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr + run.Stdout);
        (await _repo.CommitCountAsync(Ct)).Should().Be(commits + 1);
        (await _repo.LastCommitSubjectAsync(Ct)).Should().Be("archive remove zyggy/quote-2026.txt");
        (await _repo.LastCommitBodyAsync(Ct)).Should().Be("Zyggy-Tool: memory archive");
        (await _repo.GitAsync(_repo.BareDir, ["show", "--name-status", "--format=", "main"], Ct)).Split('\n')
            .Should().Equal($"D\t{Sidecar}", $"D\t{Item}");
        var sha = await _repo.GitAsync(_repo.BareDir, ["rev-parse", "main"], Ct);
        var lines = run.Stdout.Split('\n');
        lines[0].Should().Be("removed: archive/zyggy/quote-2026.txt");
        lines[1].Should().Be("removed: archive/zyggy/quote-2026.md");
        lines[3].Should().Be($"commit: {sha} pushed");
    }

    [Fact]
    public async Task Remove_Existing_InboxLineAppendedNotCommitted()
    {
        // Arrange
        await AddAsync();

        // Act
        var run = await RemoveAsync();

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        var line = run.Stdout.Split('\n')[2];
        var date = line["- [stated] ".Length..][..10];
        line.Should().Be($"- [stated] {date} (project:zyggy): Removed archived item archive/zyggy/quote-2026.txt (\"Quote 2026\")");
        File.ReadAllText(Path.Combine(_repo.PrincipalDir, "inbox", $"remember-{date}.md")).Should().EndWith(line + "\n");
        (await _repo.GitAsync(_repo.CloneDir, ["ls-files", "acme/alice/inbox"], Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Remove_Existing_ProjectFileUnchanged()
    {
        // Arrange
        await AddAsync();
        var project = Path.Combine(_repo.PrincipalDir, "business", "areas", "zyggy.md");
        var before = await File.ReadAllBytesAsync(project, Ct);

        // Act
        var run = await RemoveAsync();

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        (await File.ReadAllBytesAsync(project, Ct)).Should().Equal(before);
        (await _repo.ShowAsync("acme/alice/business/areas/zyggy.md", Ct)).Should().Be(Encoding.UTF8.GetString(before).TrimEnd('\n'));
    }

    [Fact]
    public async Task Remove_Unknown_ExitTwoNotFoundTreeUnchanged()
    {
        // Arrange
        await AddAsync();
        var before = await _repo.TreeFingerprintAsync(Ct);

        // Act
        var run = await RemoveAsync("zyggy/nothing-here");

        // Assert
        run.ExitCode.Should().Be(2, run.Stderr);
        run.Stdout.Should().BeEmpty();
        run.Stderr.Should().Be("archive: refused: not_found\n");
        (await _repo.TreeFingerprintAsync(Ct)).Should().Be(before);
    }

    [Fact]
    public async Task Remove_HooksOff_ExitTwoUnattended()
    {
        // Arrange
        await AddAsync();
        var before = await _repo.TreeFingerprintAsync(Ct);

        // Act
        var run = await RemoveAsync(env: new Dictionary<string, string?> { ["ZYGGY_HOOKS"] = "off" });

        // Assert
        run.ExitCode.Should().Be(2);
        run.Stderr.Should().Be("archive: refused: unattended run\n");
        (await _repo.TreeFingerprintAsync(Ct)).Should().Be(before);
    }

    [Fact]
    public async Task Remove_Usage_ExitFour()
    {
        // Act
        var run = await RemoveAsync("zyggy/");

        // Assert
        run.ExitCode.Should().Be(4);
        run.Stdout.Should().BeEmpty();
        run.Stderr.Should().Be("archive: 'zyggy/' is not <project>/<slug> (usage: zyggy memory archive remove <project>/<slug>)\n");
    }

    [Fact]
    public async Task Remove_RemoteAheadConflict_ExitSevenThenNextRemovePushesFirst()
    {
        // Arrange: two items; the remote then edits the first item's sidecar, so removing it conflicts on rebase.
        await AddAsync();
        await AddAsync("invoice.txt", "Roof invoice");
        var beforeConflict = await _repo.GitAsync(_repo.BareDir, ["rev-parse", "main"], Ct);
        await _repo.PushFromSecondCloneAsync(Sidecar, "edited elsewhere\n", Ct);

        // Act
        var deferred = await RemoveAsync();
        await _repo.ResetRemoteToAsync(beforeConflict, Ct);
        var next = await RemoveAsync("zyggy/roof-invoice");

        // Assert
        deferred.ExitCode.Should().Be(7, deferred.Stderr + deferred.Stdout);
        deferred.Stderr.Should().StartWith("archive: committed ").And.EndWith(", push deferred\n");
        Directory.Exists(Path.Combine(_repo.CloneDir, ".git", "rebase-merge")).Should().BeFalse();
        next.ExitCode.Should().Be(0, next.Stderr + next.Stdout);
        var subjects = (await _repo.GitAsync(_repo.BareDir, ["log", "--format=%s", "main"], Ct)).Split('\n');
        subjects[0].Should().Be("archive remove zyggy/roof-invoice.txt");
        subjects[1].Should().Be("archive remove zyggy/quote-2026.txt");
    }
}
