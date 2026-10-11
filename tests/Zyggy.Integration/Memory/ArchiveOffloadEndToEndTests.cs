using System.Text;

using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Memory;

/// <summary>
/// Plan 38 Step 8 (AC-14..AC-17, AC-24, AC-30 integration): the offload against a temporary memory repository with a local bare remote
/// (real git) and the in-memory OneDrive — two commits removing the item bytes from <c>main</c> while history keeps them, one action-log
/// row per write, two uncommitted inbox lines, <c>list</c> says <c>offloaded</c>; a failed upload changes nothing; an interrupted commit is
/// resumed without a second upload; the key is never on disk outside its file, in git or in a log.
/// </summary>
public sealed class ArchiveOffloadEndToEndTests : IAsyncLifetime
{
    private const string QuoteItem = "acme/alice/archive/zyggy/quote-2026.txt";
    private const string QuoteSidecar = "acme/alice/archive/zyggy/quote-2026.md";
    private const string PngItem = "acme/alice/archive/zyggy/screenshot.png";

    private readonly OffloadHarness _h = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => _h.InitializeAsync();

    public ValueTask DisposeAsync() => _h.DisposeAsync();

    [Fact]
    public async Task Offload_TwoIndexedItems_TwoCommitsItemPathsGoneSidecarsUpdated()
    {
        // Arrange
        var before = await _h.Repo.CommitCountAsync(Ct);
        var quoteBytes = await File.ReadAllBytesAsync(Path.Combine(_h.Repo.CloneDir, QuoteItem), Ct);

        // Act
        var (exit, console) = await _h.RunInProcessAsync();

        // Assert
        exit.Should().Be(0, console.Stderr);
        (await _h.Repo.CommitCountAsync(Ct)).Should().Be(before + 2);
        (await _h.Repo.GitAsync(_h.Repo.BareDir, ["log", "-2", "--format=%s", "main"], Ct)).Split('\n')
            .Should().Equal("archive offload zyggy/screenshot.png", "archive offload zyggy/quote-2026.txt");
        var body = await _h.Repo.GitAsync(_h.Repo.BareDir, ["log", "-1", "--format=%b", "main~1"], Ct);
        body.Should().StartWith("drive_item_id: ").And.Contain("; sha256: c5f2cab5d3b4d46c44bee6f1ee037d471c419b739908f4a05251745fb4794f88; quickxor: jAt95bF64I8MSmyO3joPMtROLIU=; size: 68 bytes")
            .And.EndWith("Zyggy-Tool: memory archive offload");
        var tree = (await _h.Repo.GitAsync(_h.Repo.BareDir, ["ls-tree", "-r", "--name-only", "main"], Ct)).Split('\n');
        tree.Should().NotContain([QuoteItem, PngItem]).And.Contain(QuoteSidecar);
        (await _h.Repo.GitAsync(_h.Repo.BareDir, ["show", "main~2:" + QuoteItem], Ct)).Should()
            .Be(Encoding.UTF8.GetString(quoteBytes).TrimEnd('\n'), "history keeps the bytes (decision 4)");
        (await _h.Repo.ShowAsync(QuoteSidecar, Ct)).Should().Contain("drive_path: Zyggy/archive/zyggy/quote-2026.txt").And.Contain("drive_quickxor: jAt95bF64I8MSmyO3joPMtROLIU=");
        (await _h.Repo.StatusAsync(Ct)).Should().Be("?? acme/alice/inbox/remember-2026-10-01.md", "only the uncommitted inbox file is left");
    }

    [Fact]
    public async Task Offload_ActionRows_OneOkRowPerWrite()
    {
        // Act
        var (exit, console) = await _h.RunInProcessAsync();

        // Assert
        exit.Should().Be(0, console.Stderr);
        var rows = _h.ActionRows();
        rows.Should().HaveCount(3);
        rows[0].Should().Contain("\"tool\":\"archive-offload-folder\"");
        rows.Skip(1).Should().OnlyContain(r => r.Contains("\"tool\":\"archive-offload-upload\"", StringComparison.Ordinal));
        rows.Should().OnlyContain(r => r.Contains("\"session_id\":\"archive-offload 2026-10-01T03:30:00Z\"", StringComparison.Ordinal)
            && r.EndsWith("\"status\":\"ok\"}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Offload_InboxLines_NotCommitted()
    {
        // Act
        await _h.RunInProcessAsync();

        // Assert
        var inbox = File.ReadAllLines(Path.Combine(_h.Repo.PrincipalDir, "inbox", "remember-2026-10-01.md"));
        inbox.Where(l => l.StartsWith("- [observed] 2026-10-01 (project:zyggy) (source: archive offload): Moved ", StringComparison.Ordinal))
            .Should().Equal(
                "- [observed] 2026-10-01 (project:zyggy) (source: archive offload): Moved archive/zyggy/quote-2026.txt to OneDrive Zyggy/archive/zyggy/quote-2026.txt",
                "- [observed] 2026-10-01 (project:zyggy) (source: archive offload): Moved archive/zyggy/screenshot.png to OneDrive Zyggy/archive/zyggy/screenshot.png");
        (await _h.Repo.GitAsync(_h.Repo.CloneDir, ["ls-files", "acme/alice/inbox"], Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Offload_ThenListBinary_ShowsOffloaded()
    {
        // Arrange
        await _h.RunInProcessAsync();

        // Act
        var run = await ZyggyCli.RunAsync(["memory", "archive", "list"], _h.Env(), null, _h.Repo.RootDir, Ct);

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        run.Stdout.Should().Be(
            "archive/zyggy/quote-2026.txt  text/plain  68 B  offloaded → Zyggy/archive/zyggy/quote-2026.txt  — Roof repair quote\n"
            + "archive/zyggy/screenshot.png  image/png  65 B  offloaded → Zyggy/archive/zyggy/screenshot.png  — Photo of the roof damage\n");
    }

    [Fact]
    public async Task Offload_UploadFails_StatusCleanRemoteUnchanged()
    {
        // Arrange: one item per run; the project folder exists, so the next write is the upload, answered 500
        _h.Configure("""{"offload": {"enabled": true, "max_items_per_run": 1}}""");
        _h.Drive.Add(Zyggy.Core.Tests.Infrastructure.StubOneDrive.RootId, "zyggy");
        _h.Drive.RefuseNextWith(500);
        var fingerprint = await _h.Repo.TreeFingerprintAsync(Ct);
        var remote = await _h.Repo.GitAsync(_h.Repo.BareDir, ["rev-parse", "main"], Ct);

        // Act
        var (exit, console) = await _h.RunInProcessAsync();

        // Assert
        exit.Should().Be(5);
        console.Stderr.Should().StartWith("offload: zyggy/quote-2026.txt: server_error");
        (await _h.Repo.TreeFingerprintAsync(Ct)).Should().Be(fingerprint);
        (await _h.Repo.GitAsync(_h.Repo.BareDir, ["rev-parse", "main"], Ct)).Should().Be(remote);
    }

    [Fact]
    public async Task Offload_CommitInterrupted_NextRunResumes()
    {
        // Arrange: a pre-commit hook makes the first run's commit fail after the upload, the sidecar write and the git rm.
        // (A stale index.lock would do too, but GitClient waits out a lock on the injected clock, which the fake clock never advances.)
        _h.Configure("""{"offload": {"enabled": true, "max_items_per_run": 1}}""");
        var hook = Path.Combine(_h.Repo.CloneDir, ".git", "hooks", "pre-commit");
        Directory.CreateDirectory(Path.GetDirectoryName(hook)!);
        await File.WriteAllTextAsync(hook, "#!/bin/sh\necho interrupted >&2\nexit 1\n", Ct);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var (first, firstConsole) = await _h.RunInProcessAsync();
        File.Delete(hook);
        var restored = File.Exists(Path.Combine(_h.Repo.PrincipalDir, "archive", "zyggy", "quote-2026.txt"));
        var sidecarAfterFirst = await File.ReadAllTextAsync(Path.Combine(_h.Repo.PrincipalDir, "archive", "zyggy", "quote-2026.md"), Ct);

        // Act
        var (second, secondConsole) = await _h.RunInProcessAsync();

        // Assert
        first.Should().Be(5, firstConsole.Stderr);
        firstConsole.Stderr.Should().StartWith("offload: zyggy/quote-2026.txt: git_error (commit_failed)");
        restored.Should().BeTrue("the item is restored from HEAD after the failed commit");
        sidecarAfterFirst.Should().Contain("offloaded: 2026-10-01", "the sidecar keeps the offload part, so the next run resumes");
        second.Should().Be(0, secondConsole.Stderr);
        secondConsole.Stdout.Should().StartWith("offloaded: archive/zyggy/quote-2026.txt -> Zyggy/archive/zyggy/quote-2026.txt\n");
        _h.Drive.Puts.Should().Be(1, "the resume verifies the existing copy and uploads nothing");
        (await _h.Repo.LastCommitSubjectAsync(Ct)).Should().Be("archive offload zyggy/quote-2026.txt");
        (await _h.Repo.StatusAsync(Ct)).Should().Be("?? acme/alice/inbox/remember-2026-10-01.md", "only the uncommitted inbox file is left");
    }

    [Fact]
    public async Task Offload_KeyNeverOnDiskOrInGitOrLog()
    {
        // Act
        await _h.RunInProcessAsync();

        // Assert: the key's PEM body and the stub's access token appear in no file of the clone, the state directory, or the git history
        var keyBody = (await File.ReadAllLinesAsync(_h.KeyFile, Ct)).Where(l => !l.StartsWith("-----", StringComparison.Ordinal)).First();
        var token = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "golden", "m365", "graph", "token-ok.json"), Ct))
            .RootElement.GetProperty("access_token").GetString()!;
        var files = Directory.EnumerateFiles(_h.Repo.CloneDir, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Concat(Directory.EnumerateFiles(_h.Repo.StateDir, "*", SearchOption.AllDirectories));
        foreach (var file in files)
        {
            var text = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file, Ct));
            text.Should().NotContain(keyBody, file).And.NotContain(token, file).And.NotContain("PRIVATE KEY", file);
        }

        var history = await _h.Repo.GitAsync(_h.Repo.BareDir, ["log", "-p", "main"], Ct);
        history.Should().NotContain(keyBody).And.NotContain(token);
    }
}
