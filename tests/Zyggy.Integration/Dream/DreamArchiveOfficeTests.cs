using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Dream;

/// <summary>
/// Plan 39 Step 5 (spec 39 AC-16 integration): with no dream code change, after <c>archive add</c> of the docx sample one <c>zyggy dream</c>
/// through fake-claude files the label line into the project's file in one commit touching no archive path; the model still cannot read
/// the archive directory and never sees an Office byte or a sidecar key; <c>list</c> says <c>indexed</c>; the prompts are unchanged.
/// </summary>
public sealed class DreamArchiveOfficeTests : IAsyncLifetime
{
    private const string Item = "acme/alice/archive/zyggy/roof-offer.docx";

    private const string Sidecar = "acme/alice/archive/zyggy/roof-offer.md";

    // SHA-256 of the committed prompt files (sha256sum, 2026-10-12): AC-16 pins that 39 changes no prompt and no prompt-version.
    private static readonly Dictionary<string, string> PromptHashes = new(StringComparer.Ordinal)
    {
        ["compression.prompt.md"] = "430027177e0f8f570d9a753d7975a3c78ddd3723afaa8094ba1b675210284dc5",
        ["compression.schema.json"] = "b4711f75799c6c58b68e0b50743eeae5e2524696011a511d200260b8e13c1fdf",
        ["filing.prompt.md"] = "56bbef1d587ea6f8ba476469e8664ad1306bf823f19d6e5a4929168d580826b3",
        ["filing.schema.json"] = "2ea1f84fb4c994916bf1332740b993c32c14eaea963ae33bb0a06fce71b222fd",
        ["migration.prompt.md"] = "25d30879a1e232a8a61cffdf61318a9aaf62c99398ccbf3d9beb6e566e40eac7",
        ["migration.schema.json"] = "5ae39b7ca0a6ebe67f8322123146b4e5c037dc4c2cd69d3d370a7f9fc7fab58e",
    };

    private readonly MemoryRepoFixture _repo = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Today => DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string LabelLine =>
        $"- [stated] {Today} (project:zyggy): Archived \"Roof offer\" (Word document, 1.3 KB) at archive/zyggy/roof-offer.docx — Contractor offer for the roof repair";

    private static byte[] Docx => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "golden", "office", "sample.docx"));

    public async ValueTask InitializeAsync()
    {
        await _repo.InitializeAsync();
        await _repo.SeedAsync("archive", Ct);
        _repo.MaterialiseScenario("dream-archive-office-ok");
        var source = await _repo.WriteSourceAsync("sample.docx", Docx);
        var add = await _repo.ArchiveAsync(Ct, null, ["add", "--project", "zyggy", "--name", "Roof offer", "--description", "Contractor offer for the roof repair", "--file", source]);
        add.ExitCode.Should().Be(0, add.Stderr);
    }

    public ValueTask DisposeAsync() => _repo.DisposeAsync();

    [Fact]
    public async Task Dream_AfterDocxAdd_FilesLabelLineIntoProjectFileOneCommitNoArchivePath()
    {
        // Act
        var run = await _repo.DreamAsync("dream-archive-office-ok", Ct);

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr + run.Stdout);
        var subjects = (await _repo.GitAsync(_repo.BareDir, ["log", "--format=%s", "main"], Ct)).Split('\n');
        subjects[0].Should().Be($"dream {Today}");
        subjects[1].Should().Be("archive add zyggy/roof-offer.docx");
        (await _repo.LastCommitPathsAsync(Ct)).Should().Equal("acme/alice/.dream/ledger.json", "acme/alice/business/areas/zyggy.md");
        var project = await _repo.ShowAsync("acme/alice/business/areas/zyggy.md", Ct);
        project.Split('\n').Should().Contain(LabelLine);
        project.Should().Contain("(Word document, ").And.Contain("archive/zyggy/roof-offer.docx");
    }

    [Fact]
    public async Task Dream_AfterDocxAdd_ItemAndSidecarBytesOnMainUnchanged()
    {
        // Arrange
        var sidecar = await _repo.ShowBytesAsync(Sidecar, Ct);

        // Act
        var run = await _repo.DreamAsync("dream-archive-office-ok", Ct);

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        (await _repo.ShowBytesAsync(Item, Ct)).Should().Equal(Docx);
        (await _repo.ShowBytesAsync(Sidecar, Ct)).Should().Equal(sidecar);
    }

    [Fact]
    public async Task Dream_AfterDocxAdd_CapturedArgumentsContainArchiveReadDeny()
    {
        // Arrange
        var capture = Path.Combine(_repo.RootDir, "args.bin");

        // Act
        var run = await _repo.DreamAsync("dream-archive-office-ok", Ct, new Dictionary<string, string?> { ["ZYGGY_FAKE_CLAUDE_CAPTURE"] = capture });

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        var arguments = FakeClaude.ReadCapture(capture).Arguments;
        var deny = arguments[arguments.ToList().IndexOf("--disallowedTools") + 1];
        deny.Split(',').Should().Contain($"Read(//{_repo.PrincipalDir.Replace('\\', '/').TrimStart('/')}/archive/**)");
    }

    [Fact]
    public async Task Dream_AfterDocxAdd_StdinNeverContainsItemBytesOrSidecarKeys()
    {
        // Arrange
        var stdinPath = Path.Combine(_repo.RootDir, "stdin.bin");

        // Act
        var run = await _repo.DreamAsync("dream-archive-office-ok", Ct, new Dictionary<string, string?> { ["ZYGGY_FAKE_CLAUDE_STDIN_CAPTURE"] = stdinPath });

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        var stdin = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(stdinPath, Ct));
        stdin.Should().Contain(LabelLine);
        stdin.Should().NotContain("[Content_Types]").And.NotContain("word/document.xml").And.NotContain("media_type:").And.NotContain("sha256:");
    }

    [Fact]
    public async Task Dream_AfterDocxAdd_ListShowsIndexed()
    {
        // Arrange
        var before = await _repo.ArchiveAsync(Ct, null, ["list"]);

        // Act
        var run = await _repo.DreamAsync("dream-archive-office-ok", Ct);
        var after = await _repo.ArchiveAsync(Ct, null, ["list"]);

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        before.Stdout.Should().Be("archive/zyggy/roof-offer.docx  application/vnd.openxmlformats-officedocument.wordprocessingml.document  1.3 KB  unindexed  — Contractor offer for the roof repair\n");
        after.Stdout.Should().Be("archive/zyggy/roof-offer.docx  application/vnd.openxmlformats-officedocument.wordprocessingml.document  1.3 KB  indexed  — Contractor offer for the roof repair\n");
    }

    [Fact]
    public void Dream_PromptVersionAndPromptsUnchanged()
    {
        // Arrange
        var prompts = Path.Combine(RepoRoot(), "src", "Zyggy.Core", "Dream", "Prompts");

        // Act
        var hashes = Directory.EnumerateFiles(prompts).ToDictionary(f => Path.GetFileName(f), f => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f))), StringComparer.Ordinal);

        // Assert
        hashes.Should().Equal(PromptHashes);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Zyggy.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Zyggy.slnx not found above the test output.");
    }
}
