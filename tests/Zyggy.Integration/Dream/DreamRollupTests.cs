using System.Globalization;

using Zyggy.Core.Dream;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Dream;

/// <summary>
/// AC-23, AC-24, AC-25 (integration): the whole backlog is already consumed, so no model call happens; the run only rolls up old
/// daily files, deletes the closed inbox file and passes auto/ and daily/ through.
/// </summary>
public sealed class DreamRollupTests : IAsyncLifetime
{
    private readonly MemoryRepoFixture _repo = new();
    private readonly DateOnly _today = DateOnly.FromDateTime(DateTime.UtcNow);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Day(int daysAgo) => _today.AddDays(-daysAgo).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private string Principal(string relative) => Path.Combine(_repo.PrincipalDir, relative);

    public async ValueTask InitializeAsync()
    {
        await _repo.InitializeAsync();
        await _repo.SeedAsync("dream", Ct);

        // Everything in the dream fixture's inbox is consumed, plus the files below; then the ledger is committed.
        var ledger = DreamLedger.Empty();
        void Write(string relative, params string[] lines)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Principal(relative))!);
            File.WriteAllText(Principal(relative), string.Concat(lines.Select(l => l + "\n")));
        }

        void Consume(string relative, DateOnly date) =>
            ledger.Consume(relative, File.ReadAllLines(Principal(relative)).Where(l => l.StartsWith("- ", StringComparison.Ordinal)).Select(LineHash.Of), date);

        foreach (var inbox in Directory.EnumerateFiles(Principal("inbox")))
        {
            Consume("inbox/" + Path.GetFileName(inbox), _today);
        }

        Write($"daily/{Day(31)}.md", $"- [observed] 09:15 session a1: work on day 31.");
        Write($"daily/{Day(32)}.md", $"- [observed] 09:15 session a2: work on day 32.");
        Write($"daily/{Day(0)}.md", $"- [observed] 08:00 session a3: today.");
        Write($"inbox/remember-{Day(10)}.md", "- [stated] 2026-09-01: an old stated fact.");
        Write($"inbox/m365-mail-backfill-{Day(1)}.md", "- [observed] 2026-09-02 [m365-mail 2026-09-02]: yesterday's backfill.");
        Consume($"daily/{Day(31)}.md", _today.AddDays(-30));
        Consume($"daily/{Day(32)}.md", _today.AddDays(-31));
        Consume($"daily/{Day(0)}.md", _today);
        Consume($"inbox/remember-{Day(10)}.md", _today.AddDays(-8));
        Consume($"inbox/m365-mail-backfill-{Day(1)}.md", _today);
        Write(".dream/ledger.json");
        await File.WriteAllTextAsync(Principal(".dream/ledger.json"), ledger.Serialize(), Ct);
        await _repo.GitAsync(_repo.CloneDir, ["add", "--all", "--", ".", ":(exclude)acme/alice/inbox"], Ct);
        await _repo.GitAsync(_repo.CloneDir, ["commit", "-q", "-m", "seed rollup"], Ct);
        await _repo.GitAsync(_repo.CloneDir, ["push", "-q", "origin", "HEAD:main"], Ct);
        _repo.SetMtime($"inbox/remember-{Day(10)}.md", TimeSpan.FromDays(9));
        _repo.SetMtime($"inbox/m365-mail-backfill-{Day(1)}.md", TimeSpan.FromDays(2));
    }

    public ValueTask DisposeAsync() => _repo.DisposeAsync();

    [Fact]
    public async Task Dream_OldDailies_ArchivedIntoMonthFileAndDeletedInCommit()
    {
        // Act
        var run = await _repo.DreamAsync("error", Ct);

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr + run.Stdout);
        foreach (var day in new[] { Day(31), Day(32) })
        {
            var month = await _repo.ShowAsync($"acme/alice/daily/{day[..7]}.md", Ct);
            month.Should().Contain($"## {day}");
            var act = () => _repo.ShowAsync($"acme/alice/daily/{day}.md", Ct);
            await act.Should().ThrowAsync<InvalidOperationException>("the rolled day file is deleted in the commit");
        }

        (await _repo.ShowAsync($"acme/alice/daily/{Day(0)}.md", Ct)).Should().Contain("today");
        DreamRunRecordStore.ReadLast(_repo.StateDir)!.Rollup.Should().Be(new DreamRollupRecord(2, 1));
    }

    [Fact]
    public async Task Dream_ClosedConsumedInboxFile_DeletedOnDiskNotInCommit()
    {
        // Act
        var run = await _repo.DreamAsync("error", Ct);

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        File.Exists(Principal($"inbox/remember-{Day(10)}.md")).Should().BeFalse();
        File.Exists(Principal($"inbox/m365-mail-backfill-{Day(1)}.md")).Should().BeTrue("dated yesterday, it is not closed");
        (await _repo.GitAsync(_repo.BareDir, ["log", "--all", "--format=%H", "--", "acme/alice/inbox"], Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Dream_AutoAndDailyChanges_CommittedAsFound()
    {
        // Arrange
        const string Auto = "# auto memory\n- prefers short answers (written by Claude Code)\n";
        await File.WriteAllTextAsync(Principal("auto/MEMORY.md"), Auto, Ct);
        // A non-fact line: a new fact line would be backlog for the model, and this test isolates pass-through.
        await File.AppendAllTextAsync(Principal($"daily/{Day(0)}.md"), "## notes written by the Stop hook\n", Ct);

        // Act
        var run = await _repo.DreamAsync("error", Ct);

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        (await _repo.ShowAsync("acme/alice/auto/MEMORY.md", Ct)).Should().Be(Auto.TrimEnd());
        (await _repo.ShowAsync($"acme/alice/daily/{Day(0)}.md", Ct)).Should().Contain("written by the Stop hook");
    }

    [Fact]
    public async Task Dream_AutoFileWithSecretLine_WithheldNamedInRecordOthersCommitted()
    {
        // Arrange
        await File.WriteAllTextAsync(Principal("auto/notes.md"), "token ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 pasted\n", Ct);
        await File.WriteAllTextAsync(Principal("auto/MEMORY.md"), "# auto memory, updated\n", Ct);

        // Act
        var run = await _repo.DreamAsync("error", Ct);

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        DreamRunRecordStore.ReadLast(_repo.StateDir)!.Withheld.Should().Equal("auto/notes.md");
        var act = () => _repo.ShowAsync("acme/alice/auto/notes.md", Ct);
        await act.Should().ThrowAsync<InvalidOperationException>();
        (await _repo.ShowAsync("acme/alice/auto/MEMORY.md", Ct)).Should().Contain("updated");
    }

    [Fact]
    public async Task Dream_EmptyBacklog_NoCommitExitZeroNothingToDo()
    {
        // Arrange: the first run does the rollup.
        (await _repo.DreamAsync("error", Ct)).ExitCode.Should().Be(0);
        var commits = await _repo.CommitCountAsync(Ct);

        // Act
        var run = await _repo.DreamAsync("error", Ct);

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        (await _repo.CommitCountAsync(Ct)).Should().Be(commits);
        DreamRunRecordStore.ReadLast(_repo.StateDir)!.Outcome.Should().Be("nothing_to_do");
    }
}
