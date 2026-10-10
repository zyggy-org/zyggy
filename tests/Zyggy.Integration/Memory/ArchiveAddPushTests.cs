using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Memory;

/// <summary>
/// Plan 37 Step 5 (AC-15, AC-16 integration): the remote moved ahead before <c>archive add</c> pushes — rebase once, or keep the commit,
/// exit 7 and push it first on the next <c>add</c>; never force. A detached head refuses before anything is written.
/// </summary>
public sealed class ArchiveAddPushTests : IAsyncLifetime
{
    private readonly MemoryRepoFixture _repo = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _repo.InitializeAsync();
        await _repo.SeedAsync("archive", Ct);
    }

    public ValueTask DisposeAsync() => _repo.DisposeAsync();

    private Task<string> Subjects() => _repo.GitAsync(_repo.BareDir, ["log", "--format=%s", "main"], Ct);

    private Task<string> RemoteMain() => _repo.GitAsync(_repo.BareDir, ["rev-parse", "main"], Ct);

    private async Task<ZyggyRun> AddAsync(string fileName, string name, string text)
    {
        var source = await _repo.WriteSourceAsync(fileName, System.Text.Encoding.UTF8.GetBytes(text));
        return await _repo.ArchiveAsync(Ct, null, ["add", "--project", "zyggy", "--name", name, "--description", "Roof repair quote", "--file", source]);
    }

    [Fact]
    public async Task Add_RemoteAheadNoConflict_RebasedOncePushedExitZero()
    {
        // Arrange
        await _repo.PushFromSecondCloneAsync("acme/alice/private/people/dana.md", "- [stated] 2026-10-04: Dana moved.\n", Ct);

        // Act
        var run = await AddAsync("quote.txt", "Quote 2026", "Quote for the Zyggy roof repair.\n");

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr + run.Stdout);
        var subjects = (await Subjects()).Split('\n');
        subjects[0].Should().Be("archive add zyggy/quote-2026.txt");
        subjects[1].Should().Be("edit from elsewhere");
        run.Stdout.Should().EndWith($"commit: {await RemoteMain()} pushed\n");
    }

    [Fact]
    public async Task Add_RemoteAheadConflict_ExitSevenLocalCommitKeptRebaseAbortedNoForce()
    {
        // Arrange: the other clone pushes a different file at the same archive path, so the rebase conflicts.
        var other = await _repo.PushFromSecondCloneAsync("acme/alice/archive/zyggy/quote-2026.txt", "written elsewhere\n", Ct);

        // Act
        var run = await AddAsync("quote.txt", "Quote 2026", "Quote for the Zyggy roof repair.\n");

        // Assert
        run.ExitCode.Should().Be(7, run.Stderr + run.Stdout);
        var local = await _repo.GitAsync(_repo.CloneDir, ["rev-parse", "HEAD"], Ct);
        run.Stderr.Should().Be($"archive: committed {local}, push deferred\n");
        run.Stdout.Should().EndWith($"commit: {local} push deferred\n");
        (await RemoteMain()).Should().Be(other, "nothing was forced over the remote");
        (await _repo.GitAsync(_repo.CloneDir, ["log", "-1", "--format=%s"], Ct)).Should().Be("archive add zyggy/quote-2026.txt");
        Directory.Exists(Path.Combine(_repo.CloneDir, ".git", "rebase-merge")).Should().BeFalse("the rebase was aborted");
        Directory.Exists(Path.Combine(_repo.CloneDir, ".git", "rebase-apply")).Should().BeFalse();
    }

    [Fact]
    public async Task Add_AfterDeferredPush_NextAddPushesFirst()
    {
        // Arrange: a deferred push, then the conflicting remote commit disappears.
        var seed = await RemoteMain();
        await _repo.PushFromSecondCloneAsync("acme/alice/archive/zyggy/quote-2026.txt", "written elsewhere\n", Ct);
        var deferred = await AddAsync("quote.txt", "Quote 2026", "Quote for the Zyggy roof repair.\n");
        deferred.ExitCode.Should().Be(7, deferred.Stderr);
        await _repo.ResetRemoteToAsync(seed, Ct);

        // Act
        var run = await AddAsync("invoice.txt", "Roof invoice", "Invoice for the Zyggy roof repair.\n");

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr + run.Stdout);
        var subjects = (await Subjects()).Split('\n');
        subjects[0].Should().Be("archive add zyggy/roof-invoice.txt");
        subjects[1].Should().Be("archive add zyggy/quote-2026.txt");
        subjects[2].Should().Be("seed");
    }

    [Fact]
    public async Task Add_DetachedHead_ExitSixNotOnBranchNothingWritten()
    {
        // Arrange
        await _repo.GitAsync(_repo.CloneDir, ["checkout", "-q", "--detach"], Ct);
        var source = await _repo.WriteSourceAsync("quote.txt", "Quote for the Zyggy roof repair.\n"u8.ToArray());
        var before = await _repo.TreeFingerprintAsync(Ct);

        // Act
        var run = await _repo.ArchiveAsync(Ct, null, ["add", "--project", "zyggy", "--name", "Quote 2026", "--description", "Roof repair quote", "--file", source]);

        // Assert
        run.ExitCode.Should().Be(6, run.Stderr + run.Stdout);
        run.Stdout.Should().BeEmpty();
        run.Stderr.Should().Be("archive: git error: not_on_branch\n");
        (await _repo.TreeFingerprintAsync(Ct)).Should().Be(before);
    }
}
