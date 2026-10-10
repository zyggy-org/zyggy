using System.Globalization;
using System.Text;

using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Dream;

/// <summary>
/// Plan 37 Step 9 (spec 37 AC-16, AC-18, AC-21, AC-22 integration): after <c>zyggy memory archive add</c>, one <c>zyggy dream</c> through
/// fake-claude files the index line into the project's file without ever opening, committing or exposing the archived item.
/// </summary>
public sealed class DreamArchiveTests : IAsyncLifetime
{
    private const string ItemText = "Quote for the Zyggy roof repair.\nTotal 1 234,00 EUR, valid 30 days.\n";

    private const string Item = "acme/alice/archive/zyggy/quote-2026.txt";

    private const string Sidecar = "acme/alice/archive/zyggy/quote-2026.md";

    private readonly MemoryRepoFixture _repo = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Today => DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string IndexLine(string project) =>
        $"- [stated] {Today} (project:{project}): Archived \"Quote 2026\" (text/plain, 68 B) at archive/{project}/quote-2026.txt — Roof repair quote";

    public async ValueTask InitializeAsync()
    {
        await _repo.InitializeAsync();
        await _repo.SeedAsync("archive", Ct);
        foreach (var name in new[] { "dream-archive-ok", "dream-archive-new-file-ok", "dream-archive-dropped" })
        {
            _repo.MaterialiseScenario(name);
        }
    }

    public ValueTask DisposeAsync() => _repo.DisposeAsync();

    private async Task<ZyggyRun> AddAsync(string project = "zyggy")
    {
        var source = await _repo.WriteSourceAsync("quote.txt", Encoding.UTF8.GetBytes(ItemText));
        return await _repo.ArchiveAsync(Ct, null, ["add", "--project", project, "--name", "Quote 2026", "--description", "Roof repair quote", "--file", source]);
    }

    private async Task AddedAsync(string project = "zyggy")
    {
        var run = await AddAsync(project);
        run.ExitCode.Should().Be(0, run.Stderr);
    }

    private Task<string[]> SubjectsAsync() =>
        _repo.GitAsync(_repo.BareDir, ["log", "--format=%s", "main"], Ct).ContinueWith(t => t.Result.Split('\n'), TaskScheduler.Default);

    [Fact]
    public async Task Dream_AfterArchiveAdd_FilesIndexLineIntoProjectFileOneCommit()
    {
        // Arrange
        await AddedAsync();

        // Act
        var run = await _repo.DreamAsync("dream-archive-ok", Ct);

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr + run.Stdout);
        var subjects = await SubjectsAsync();
        subjects[0].Should().Be($"dream {Today}");
        subjects[1].Should().Be("archive add zyggy/quote-2026.txt");
        (await _repo.ShowAsync("acme/alice/business/areas/zyggy.md", Ct)).Split('\n').Should().Contain(IndexLine("zyggy"));
        (await _repo.LastCommitPathsAsync(Ct)).Should().Equal("acme/alice/.dream/ledger.json", "acme/alice/business/areas/zyggy.md");
    }

    [Fact]
    public async Task Dream_AfterArchiveAdd_ItemBytesOnMainUnchanged()
    {
        // Arrange
        await AddedAsync();
        var item = await _repo.ShowBytesAsync(Item, Ct);
        var sidecar = await _repo.ShowBytesAsync(Sidecar, Ct);

        // Act
        var run = await _repo.DreamAsync("dream-archive-ok", Ct);

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        item.Should().Equal(Encoding.UTF8.GetBytes(ItemText));
        (await _repo.ShowBytesAsync(Item, Ct)).Should().Equal(item);
        (await _repo.ShowBytesAsync(Sidecar, Ct)).Should().Equal(sidecar);
    }

    [Fact]
    public async Task Dream_AfterArchiveAdd_CapturedArgumentsContainArchiveReadDeny()
    {
        // Arrange
        await AddedAsync();
        var capture = Path.Combine(_repo.RootDir, "args.bin");

        // Act
        var run = await _repo.DreamAsync("dream-archive-ok", Ct, new Dictionary<string, string?> { ["ZYGGY_FAKE_CLAUDE_CAPTURE"] = capture });

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        var arguments = FakeClaude.ReadCapture(capture).Arguments;
        var deny = arguments[arguments.ToList().IndexOf("--disallowedTools") + 1];
        deny.Split(',').Should().Contain($"Read(//{_repo.PrincipalDir.Replace('\\', '/').TrimStart('/')}/archive/**)");
        arguments.Should().ContainInConsecutiveOrder("--add-dir", _repo.PrincipalDir);
    }

    [Fact]
    public async Task Dream_AfterArchiveAdd_StdinNeverContainsItemOrSidecarText()
    {
        // Arrange
        await AddedAsync();
        var stdinPath = Path.Combine(_repo.RootDir, "stdin.bin");

        // Act
        var run = await _repo.DreamAsync("dream-archive-ok", Ct, new Dictionary<string, string?> { ["ZYGGY_FAKE_CLAUDE_STDIN_CAPTURE"] = stdinPath });

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        var stdin = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(stdinPath, Ct));
        stdin.Should().Contain(IndexLine("zyggy"));
        stdin.Should().NotContain("Quote for the Zyggy roof repair.").And.NotContain("media_type:").And.NotContain("sha256:");
    }

    [Fact]
    public async Task Dream_AfterArchiveAdd_ListShowsIndexed()
    {
        // Arrange
        await AddedAsync();
        var before = await _repo.ArchiveAsync(Ct, null, ["list"]);

        // Act
        var run = await _repo.DreamAsync("dream-archive-ok", Ct);
        var after = await _repo.ArchiveAsync(Ct, null, ["list"]);

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        before.Stdout.Should().Be("archive/zyggy/quote-2026.txt  text/plain  68 B  unindexed  — Roof repair quote\n");
        after.Stdout.Should().Be("archive/zyggy/quote-2026.txt  text/plain  68 B  indexed  — Roof repair quote\n");
    }

    [Fact]
    public async Task Dream_NewProjectFile_CreatedWithTheLine()
    {
        // Arrange
        await AddedAsync("house-move");

        // Act
        var run = await _repo.DreamAsync("dream-archive-new-file-ok", Ct);

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr + run.Stdout);
        (await _repo.ShowAsync("acme/alice/private/areas/house-move.md", Ct)).Split('\n').Should().Contain(IndexLine("house-move"));
        (await _repo.LastCommitPathsAsync(Ct)).Should().NotContain(p => p.Contains("/archive/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Dream_ScenarioDropsIndexLine_ExitFiveStatedDroppedNothingCommitted()
    {
        // Arrange
        await AddedAsync();
        var main = await _repo.GitAsync(_repo.BareDir, ["rev-parse", "main"], Ct);

        // Act
        var run = await _repo.DreamAsync("dream-archive-dropped", Ct);

        // Assert
        run.ExitCode.Should().Be(5, run.Stderr + run.Stdout);
        (run.Stdout + run.Stderr).Should().Contain("stated_dropped");
        (await _repo.GitAsync(_repo.BareDir, ["rev-parse", "main"], Ct)).Should().Be(main);
        (await _repo.ArchiveAsync(Ct, null, ["list"])).Stdout.Should().Contain("  unindexed  ");
    }

    [Fact]
    public async Task Dream_AfterDeferredArchivePush_DreamPushesArchiveCommitFirst()
    {
        // Arrange: the archive commit's push is deferred by a conflicting remote commit, which then disappears.
        var seed = await _repo.GitAsync(_repo.BareDir, ["rev-parse", "main"], Ct);
        await _repo.PushFromSecondCloneAsync(Item, "written elsewhere\n", Ct);
        var deferred = await AddAsync();
        deferred.ExitCode.Should().Be(7, deferred.Stderr);
        await _repo.ResetRemoteToAsync(seed, Ct);

        // Act
        var run = await _repo.DreamAsync("dream-archive-ok", Ct);

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr + run.Stdout);
        var subjects = await SubjectsAsync();
        subjects[0].Should().Be($"dream {Today}");
        subjects[1].Should().Be("archive add zyggy/quote-2026.txt");
        subjects[2].Should().Be("seed");
    }
}
