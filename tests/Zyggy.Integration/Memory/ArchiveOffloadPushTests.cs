using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Memory;

/// <summary>
/// Plan 38 Step 8 (AC-14 push, integration): the offload's push follows 37's rule — a remote ahead without conflict is rebased once and
/// pushed; a conflict defers the push (exit 7) and stops the run after that item; the next <c>archive</c> run pushes the deferred commit first.
/// </summary>
public sealed class ArchiveOffloadPushTests : IAsyncLifetime
{
    private const string QuoteSidecar = "acme/alice/archive/zyggy/quote-2026.md";

    private readonly OffloadHarness _h = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => _h.InitializeAsync();

    public ValueTask DisposeAsync() => _h.DisposeAsync();

    [Fact]
    public async Task Offload_RemoteAheadNoConflict_RebasedOncePushed()
    {
        // Arrange
        await _h.Repo.PushFromSecondCloneAsync("acme/alice/business/areas/other.md", "---\nname: other\ndescription: other\nupdated: 2026-10-01\n---\n", Ct);

        // Act
        var (exit, console) = await _h.RunInProcessAsync();

        // Assert
        exit.Should().Be(0, console.Stderr);
        (await _h.Repo.GitAsync(_h.Repo.BareDir, ["log", "-3", "--format=%s", "main"], Ct)).Split('\n')
            .Should().Equal("archive offload zyggy/screenshot.png", "archive offload zyggy/quote-2026.txt", "edit from elsewhere");
        console.Stdout.Should().NotContain("push deferred");
    }

    [Fact]
    public async Task Offload_RemoteAheadConflict_ExitSevenStopsAfterFirstItem()
    {
        // Arrange: the remote changed the first item's sidecar, so its offload commit conflicts on rebase
        await _h.Repo.PushFromSecondCloneAsync(QuoteSidecar, "edited elsewhere\n", Ct);

        // Act
        var (exit, console) = await _h.RunInProcessAsync();

        // Assert
        exit.Should().Be(7, console.Stderr);
        console.Stdout.Should().StartWith("offloaded: archive/zyggy/quote-2026.txt -> Zyggy/archive/zyggy/quote-2026.txt\ncommit: ").And.EndWith(" push deferred\n");
        File.Exists(Path.Combine(_h.Repo.PrincipalDir, "archive", "zyggy", "screenshot.png")).Should().BeTrue("the run stops after a deferred push");
        _h.Drive.Puts.Should().Be(1);
        Directory.Exists(Path.Combine(_h.Repo.CloneDir, ".git", "rebase-merge")).Should().BeFalse();
    }

    [Fact]
    public async Task Offload_AfterDeferredPush_NextArchiveAddPushesFirst()
    {
        // Arrange: a deferred offload commit, then the remote rolled back so it no longer conflicts
        var before = await _h.Repo.GitAsync(_h.Repo.BareDir, ["rev-parse", "main"], Ct);
        await _h.Repo.PushFromSecondCloneAsync(QuoteSidecar, "edited elsewhere\n", Ct);
        var (deferred, _) = await _h.RunInProcessAsync();
        await _h.Repo.ResetRemoteToAsync(before, Ct);
        var source = await _h.Repo.WriteSourceAsync("gutter.txt", "Quote for the gutter.\n"u8.ToArray());

        // Act: 37's add, unchanged code, from the built binary
        var add = await ZyggyCli.RunAsync(
            ["memory", "archive", "add", "--project", "zyggy", "--name", "Gutter quote", "--description", "Gutter quote for the record", "--file", source],
            _h.Env(), null, _h.Repo.RootDir, Ct);

        // Assert
        deferred.Should().Be(7);
        add.ExitCode.Should().Be(0, add.Stderr);
        (await _h.Repo.GitAsync(_h.Repo.BareDir, ["log", "-2", "--format=%s", "main"], Ct)).Split('\n')
            .Should().Equal("archive add zyggy/gutter-quote.txt", "archive offload zyggy/quote-2026.txt");
    }
}
