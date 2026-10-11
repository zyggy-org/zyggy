using System.Globalization;
using System.Text;

using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Dream;

/// <summary>
/// Plan 38 Step 10 (spec 38 AC-26, AC-28 integration): after an offload, one <c>zyggy dream</c> through fake-claude files the
/// <c>Moved …</c> line into the project's file in one commit touching no archive path — with the dream's code unchanged; the index digest
/// then lists the project file and shows no archive path, sidecar or URL.
/// </summary>
public sealed class DreamAfterOffloadTests : IAsyncLifetime
{
    private readonly OffloadHarness _h = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Today => DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public async ValueTask InitializeAsync()
    {
        await _h.InitializeAsync();
        _h.Repo.MaterialiseScenario("dream-offload-ok");

        // The dream runs on today's date; the offload's inbox line must carry the same date. One item, so one inbox line to file.
        _h.Clock.SetUtcNow(DateTimeOffset.UtcNow);
        _h.Configure("""{"offload": {"enabled": true, "max_items_per_run": 1}}""");
        var (exit, console) = await _h.RunInProcessAsync();
        exit.Should().Be(0, console.Stderr);
    }

    public ValueTask DisposeAsync() => _h.DisposeAsync();

    [Fact]
    public async Task Dream_AfterOffload_FilesMovedLineOneCommitNoArchivePath()
    {
        // Act
        var run = await _h.Repo.DreamAsync("dream-offload-ok", Ct);

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr + run.Stdout);
        var subjects = (await _h.Repo.GitAsync(_h.Repo.BareDir, ["log", "-2", "--format=%s", "main"], Ct)).Split('\n');
        subjects.Should().Equal($"dream {Today}", "archive offload zyggy/quote-2026.txt");
        (await _h.Repo.LastCommitPathsAsync(Ct)).Should().Equal("acme/alice/.dream/ledger.json", "acme/alice/business/areas/zyggy.md");
        (await _h.Repo.ShowAsync("acme/alice/business/areas/zyggy.md", Ct)).Split('\n')
            .Should().Contain($"- [observed] {Today} [archive offload {Today}]: Moved archive/zyggy/quote-2026.txt to OneDrive Zyggy/archive/zyggy/quote-2026.txt");
    }

    [Fact]
    public async Task Dream_AfterOffload_SnapshotHasNoItemForOffloadedPath()
    {
        // Arrange
        var stdinPath = Path.Combine(_h.Repo.RootDir, "stdin.bin");

        // Act
        var run = await _h.Repo.DreamAsync("dream-offload-ok", Ct, new Dictionary<string, string?> { ["ZYGGY_FAKE_CLAUDE_STDIN_CAPTURE"] = stdinPath });

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        var stdin = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(stdinPath, Ct));
        stdin.Should().Contain("Moved archive/zyggy/quote-2026.txt to OneDrive Zyggy/archive/zyggy/quote-2026.txt");
        stdin.Should().NotContain("Quote for the Zyggy roof repair.").And.NotContain("drive_item_id").And.NotContain("drive_url").And.NotContain("https://");
    }

    [Fact]
    public async Task Dream_AfterOffload_ArgumentsStillDenyArchiveRead()
    {
        // Arrange
        var capture = Path.Combine(_h.Repo.RootDir, "args.bin");

        // Act
        var run = await _h.Repo.DreamAsync("dream-offload-ok", Ct, new Dictionary<string, string?> { ["ZYGGY_FAKE_CLAUDE_CAPTURE"] = capture });

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        var arguments = FakeClaude.ReadCapture(capture).Arguments;
        var deny = arguments[arguments.ToList().IndexOf("--disallowedTools") + 1];
        deny.Split(',').Should().Contain($"Read(//{_h.Repo.PrincipalDir.Replace('\\', '/').TrimStart('/')}/archive/**)");
    }

    [Fact]
    public async Task Digest_IndexAfterOffloadAndDream_ProjectFileListedNoArchivePathNoUrl()
    {
        // Arrange
        var dream = await _h.Repo.DreamAsync("dream-offload-ok", Ct);
        dream.ExitCode.Should().Be(0, dream.Stderr);

        // Act
        var run = await ZyggyCli.RunAsync(["memory", "digest", "index"], _h.Repo.ArchiveEnv(), null, _h.Repo.RootDir, Ct);

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        Encoding.UTF8.GetByteCount(run.Stdout).Should().BeLessThanOrEqualTo(6000);
        run.Stdout.Should().Contain("- business/areas/zyggy.md — ");
        run.Stdout.Should().NotContain("archive/").And.NotContain("Zyggy/archive").And.NotContain("https://");
    }
}
