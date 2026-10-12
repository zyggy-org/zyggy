using Zyggy.Core.Memory;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Memory;

/// <summary>
/// Plan 39 Step 4 (AC-14 integration): <c>zyggy memory archive add</c> of a Word, Excel and PowerPoint document from the built binary
/// against a seeded clone and its bare remote — the item byte-equal on <c>main</c>, a sidecar whose <c>media_type</c> is the wire string,
/// the label index line in the uncommitted inbox, one <c>archive add</c> commit of exactly two paths with the wire string in its body.
/// </summary>
public sealed class ArchiveAddOfficeCommandTests : IAsyncLifetime
{
    private const string DocxWire = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    // Hand-computed with sha256sum (tests/golden/README.md, office/).
    private const string DocxSha256 = "fa0d4998a5b647d23edf5ec41eaa6a1192341017c46d5f465a4208fc5345e0ca";

    private const string ItemPath = "acme/alice/archive/zyggy/roof-offer.docx";

    private const string SidecarPath = "acme/alice/archive/zyggy/roof-offer.md";

    private readonly MemoryRepoFixture _repo = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _repo.InitializeAsync();
        await _repo.SeedAsync("archive", Ct);
    }

    public ValueTask DisposeAsync() => _repo.DisposeAsync();

    internal static byte[] Sample(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "golden", "office", name));

    private Task<ZyggyRun> AddAsync(string source, string name, string description, params string[] extra) =>
        _repo.ArchiveAsync(Ct, null, ["add", "--project", "zyggy", "--name", name, "--description", description, "--file", source, .. extra]);

    private async Task<ZyggyRun> AddDocxAsync(string sourceName = "sample.docx")
    {
        var source = await _repo.WriteSourceAsync(sourceName, Sample("sample.docx"));
        return await AddAsync(source, "Roof offer", "Contractor offer for the roof repair");
    }

    [Fact]
    public async Task Add_Docx_ItemBytesEqualSampleOnMainOneCommitOfTwoPaths()
    {
        // Arrange
        var commits = await _repo.CommitCountAsync(Ct);

        // Act
        var run = await AddDocxAsync();

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr + run.Stdout);
        (await _repo.CommitCountAsync(Ct)).Should().Be(commits + 1);
        (await _repo.LastCommitSubjectAsync(Ct)).Should().Be("archive add zyggy/roof-offer.docx");
        (await _repo.LastCommitBodyAsync(Ct)).Should().Be($"media: {DocxWire}; size: 1289 bytes; sha256: {DocxSha256}\n\nZyggy-Tool: memory archive");
        (await _repo.LastCommitPathsAsync(Ct)).Should().Equal(ItemPath, SidecarPath);
        (await _repo.ShowBytesAsync(ItemPath, Ct)).Should().Equal(Sample("sample.docx"));
    }

    [Fact]
    public async Task Add_Xlsx_StoredAsXlsxWithSheetType()
    {
        // Arrange
        var source = await _repo.WriteSourceAsync("sample.xlsx", Sample("sample.xlsx"));

        // Act
        var run = await AddAsync(source, "Roof budget", "Cost lines for the roof repair");

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr + run.Stdout);
        (await _repo.LastCommitSubjectAsync(Ct)).Should().Be("archive add zyggy/roof-budget.xlsx");
        (await _repo.ShowBytesAsync("acme/alice/archive/zyggy/roof-budget.xlsx", Ct)).Should().Equal(Sample("sample.xlsx"));
        MemoryFileReader.Parse(await _repo.ShowAsync("acme/alice/archive/zyggy/roof-budget.md", Ct) + "\n").UnknownKeys["media_type"]
            .Should().Be("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
    }

    [Fact]
    public async Task Add_Pptx_StoredAsPptxWithPresentationType()
    {
        // Arrange
        var source = await _repo.WriteSourceAsync("sample.pptx", Sample("sample.pptx"));

        // Act
        var run = await AddAsync(source, "Roof pitch", "Slides shown to the contractor");

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr + run.Stdout);
        (await _repo.LastCommitSubjectAsync(Ct)).Should().Be("archive add zyggy/roof-pitch.pptx");
        (await _repo.ShowBytesAsync("acme/alice/archive/zyggy/roof-pitch.pptx", Ct)).Should().Equal(Sample("sample.pptx"));
        MemoryFileReader.Parse(await _repo.ShowAsync("acme/alice/archive/zyggy/roof-pitch.md", Ct) + "\n").UnknownKeys["media_type"]
            .Should().Be("application/vnd.openxmlformats-officedocument.presentationml.presentation");
    }

    [Fact]
    public async Task Add_Docx_SidecarMediaTypeIsWireStringAndKeysInOrder()
    {
        // Act
        var run = await AddDocxAsync();

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr + run.Stdout);
        var file = MemoryFileReader.Parse(await _repo.ShowAsync(SidecarPath, Ct) + "\n");
        file.UnknownKeys.Keys.Should().Equal("project", "media_type", "size_bytes", "sha256", "archived", "source_name");
        var sidecar = ArchiveSidecar.TryParse(file);
        sidecar.Should().NotBeNull();
        sidecar!.MediaType.Should().Be(ArchiveMediaType.Docx);
        sidecar.SourceName.Should().Be("sample.docx");
        sidecar.SizeBytes.Should().Be(1289);
        sidecar.Sha256.Should().Be(DocxSha256);
    }

    [Fact]
    public async Task Add_Docx_InboxLineHasLabelNotWireString()
    {
        // Act
        var run = await AddDocxAsync();

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr + run.Stdout);
        var line = run.Stdout.Split('\n')[2];
        var date = line["- [stated] ".Length..][..10];
        line.Should().Be($"- [stated] {date} (project:zyggy): Archived \"Roof offer\" (Word document, 1.3 KB) at archive/zyggy/roof-offer.docx — Contractor offer for the roof repair");
        var inbox = Path.Combine(_repo.PrincipalDir, "inbox", $"remember-{date}.md");
        var text = await File.ReadAllTextAsync(inbox, Ct);
        text.Should().EndWith("---\n" + line + "\n");
        text.Should().Contain("(Word document, ").And.NotContain("application/vnd");
        (await _repo.GitAsync(_repo.CloneDir, ["ls-files", "acme/alice/inbox"], Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Add_DocxRenamedZip_StoredAsDocxSourceNameZip()
    {
        // Act
        var run = await AddDocxAsync("report.zip");

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr + run.Stdout);
        (await _repo.LastCommitPathsAsync(Ct)).Should().Equal(ItemPath, SidecarPath);
        var sidecar = ArchiveSidecar.TryParse(MemoryFileReader.Parse(await _repo.ShowAsync(SidecarPath, Ct) + "\n"))!;
        sidecar.MediaType.Should().Be(ArchiveMediaType.Docx);
        sidecar.SourceName.Should().Be("report.zip");
    }

    [Fact]
    public async Task Add_Docx_StdoutFourLinesInOrder()
    {
        // Act
        var run = await AddDocxAsync();

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr + run.Stdout);
        run.Stderr.Should().BeEmpty();
        var sha = await _repo.GitAsync(_repo.BareDir, ["rev-parse", "main"], Ct);
        var lines = run.Stdout.Split('\n');
        lines.Should().HaveCount(5);
        lines[0].Should().Be("archived: archive/zyggy/roof-offer.docx");
        lines[1].Should().Be("sidecar: archive/zyggy/roof-offer.md");
        lines[2].Should().StartWith("- [stated] ");
        lines[3].Should().Be($"commit: {sha} pushed");
        lines[4].Should().BeEmpty();
    }

    [Fact]
    public async Task Add_ThreeOfficeItems_ListShowsThreeUnindexedRowsWithWireStrings()
    {
        // Arrange
        (await AddDocxAsync()).ExitCode.Should().Be(0);
        (await AddAsync(await _repo.WriteSourceAsync("sample.xlsx", Sample("sample.xlsx")), "Roof budget", "Cost lines for the roof repair")).ExitCode.Should().Be(0);
        (await AddAsync(await _repo.WriteSourceAsync("sample.pptx", Sample("sample.pptx")), "Roof pitch", "Slides shown to the contractor")).ExitCode.Should().Be(0);

        // Act
        var run = await _repo.ArchiveAsync(Ct, null, ["list", "--unindexed"]);

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        run.Stdout.Should().Be(
            "archive/zyggy/roof-budget.xlsx  application/vnd.openxmlformats-officedocument.spreadsheetml.sheet  2.2 KB  unindexed  — Cost lines for the roof repair\n"
            + "archive/zyggy/roof-offer.docx  application/vnd.openxmlformats-officedocument.wordprocessingml.document  1.3 KB  unindexed  — Contractor offer for the roof repair\n"
            + "archive/zyggy/roof-pitch.pptx  application/vnd.openxmlformats-officedocument.presentationml.presentation  1.3 KB  unindexed  — Slides shown to the contractor\n");
    }
}
