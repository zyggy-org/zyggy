using Zyggy.Core.Dream;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Dream;

/// <summary>AC-20 (integration): the remote moved ahead; rebase once, or defer with exit 7 and push first next time; never force.</summary>
public sealed class DreamPushTests : IAsyncLifetime
{
    private readonly MemoryRepoFixture _repo = new();
    private string _seed = string.Empty;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _repo.InitializeAsync();
        await _repo.SeedAsync("dream", Ct);
        _seed = await _repo.GitAsync(_repo.BareDir, ["rev-parse", "main"], Ct);
    }

    public ValueTask DisposeAsync() => _repo.DisposeAsync();

    private Task<string> Subjects() => _repo.GitAsync(_repo.BareDir, ["log", "--format=%s", "main"], Ct);

    [Fact]
    public async Task Dream_RemoteAheadNoConflict_RebasedOncePushedExitZero()
    {
        // Arrange
        await _repo.PushFromSecondCloneAsync("acme/alice/agents.md", "- [stated] 2026-10-04: edited elsewhere.\n", Ct);

        // Act
        var run = await _repo.DreamAsync("dream-file-ok", Ct);

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr + run.Stdout);
        var subjects = (await Subjects()).Split('\n');
        subjects[0].Should().StartWith("dream ");
        subjects[1].Should().Be("edit from elsewhere");
        DreamRunRecordStore.ReadLast(_repo.StateDir)!.Pushed.Should().BeTrue();
    }

    [Fact]
    public async Task Dream_RemoteAheadConflict_ExitSevenLocalCommitKeptRebaseAbortedNoForce()
    {
        // Arrange: the other clone appends to carol.md, where the dream appends too.
        var carol = await File.ReadAllTextAsync(Path.Combine(_repo.PrincipalDir, "private", "people", "carol.md"), Ct);
        var other = await _repo.PushFromSecondCloneAsync("acme/alice/private/people/carol.md", carol + "- [stated] 2026-10-04: conflicting line.\n", Ct);

        // Act
        var run = await _repo.DreamAsync("dream-file-ok", Ct);

        // Assert
        run.ExitCode.Should().Be(7, run.Stderr + run.Stdout);
        (await _repo.GitAsync(_repo.BareDir, ["rev-parse", "main"], Ct)).Should().Be(other, "nothing was forced over the remote");
        (await _repo.GitAsync(_repo.CloneDir, ["log", "-1", "--format=%s"], Ct)).Should().StartWith("dream ");
        Directory.Exists(Path.Combine(_repo.CloneDir, ".git", "rebase-merge")).Should().BeFalse("the rebase was aborted");
        Directory.Exists(Path.Combine(_repo.CloneDir, ".git", "rebase-apply")).Should().BeFalse();
        DreamRunRecordStore.ReadLast(_repo.StateDir)!.Pushed.Should().BeFalse();
    }

    [Fact]
    public async Task Dream_AfterDeferredPush_NextRunPushesFirst()
    {
        // Arrange: a deferred push, then the conflicting remote commit is withdrawn.
        var carol = await File.ReadAllTextAsync(Path.Combine(_repo.PrincipalDir, "private", "people", "carol.md"), Ct);
        await _repo.PushFromSecondCloneAsync("acme/alice/private/people/carol.md", carol + "- [stated] 2026-10-04: conflicting line.\n", Ct);
        (await _repo.DreamAsync("dream-file-ok", Ct)).ExitCode.Should().Be(7);
        var dreamCommit = await _repo.GitAsync(_repo.CloneDir, ["rev-parse", "HEAD"], Ct);
        await _repo.ResetRemoteToAsync(_seed, Ct);

        // Act
        var run = await _repo.DreamAsync("error", Ct);

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr + run.Stdout);
        (await _repo.GitAsync(_repo.BareDir, ["rev-parse", "main"], Ct)).Should().Be(dreamCommit);
        DreamRunRecordStore.ReadLast(_repo.StateDir)!.Outcome.Should().Be("nothing_to_do");
    }
}
