using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

using Zyggy.Core.Memory;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Memory;

/// <summary>
/// Plan 37 Step 5 (AC-13..AC-15, AC-17 integration): <c>zyggy memory archive add</c> from the built binary against a seeded clone and its
/// bare remote — one <c>archive add</c> commit of exactly the item and its sidecar, pushed; the index line in the inbox, never committed.
/// </summary>
public sealed class ArchiveAddCommandTests : IAsyncLifetime
{
    private const string TextItem = "Quote for the Zyggy roof repair.\nTotal 1 234,00 EUR, valid 30 days.\n";

    // Hand-computed with sha256sum (tests/golden/README.md, archive/).
    private const string TextSha256 = "c5f2cab5d3b4d46c44bee6f1ee037d471c419b739908f4a05251745fb4794f88";

    private const string ItemPath = "acme/alice/archive/zyggy/quote-2026.txt";

    private const string SidecarPath = "acme/alice/archive/zyggy/quote-2026.md";

    private readonly MemoryRepoFixture _repo = new();

    public static bool IsLinux => OperatingSystem.IsLinux();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _repo.InitializeAsync();
        await _repo.SeedAsync("archive", Ct);
    }

    public ValueTask DisposeAsync() => _repo.DisposeAsync();

    private static string UtcToday() => DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    internal static byte[] Png()
    {
        var bytes = new byte[65];
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), 2);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20), 2);
        bytes[24] = 8;
        bytes[25] = 2;
        return bytes;
    }

    private Task<ZyggyRun> AddAsync(string source, string project = "zyggy", string name = "Quote 2026", string description = "Roof repair quote", params string[] extra) =>
        _repo.ArchiveAsync(Ct, null, ["add", "--project", project, "--name", name, "--description", description, "--file", source, .. extra]);

    private async Task<string> TextSourceAsync() => await _repo.WriteSourceAsync("quote.txt", Encoding.UTF8.GetBytes(TextItem));

    [Fact]
    public async Task Add_Text_ItemAndSidecarOnMainInOneCommitOfTwoPaths()
    {
        // Arrange
        var source = await TextSourceAsync();
        var before = UtcToday();
        var commits = await _repo.CommitCountAsync(Ct);

        // Act
        var run = await AddAsync(source);

        // Assert
        var after = UtcToday();
        run.ExitCode.Should().Be(0, run.Stderr + run.Stdout);
        (await _repo.CommitCountAsync(Ct)).Should().Be(commits + 1);
        (await _repo.LastCommitSubjectAsync(Ct)).Should().Be("archive add zyggy/quote-2026.txt");
        (await _repo.LastCommitBodyAsync(Ct)).Should().Be($"media: text/plain; size: 68 bytes; sha256: {TextSha256}\n\nZyggy-Tool: memory archive");
        (await _repo.LastCommitPathsAsync(Ct)).Should().Equal(SidecarPath, ItemPath);
        (await _repo.ShowBytesAsync(ItemPath, Ct)).Should().Equal(Encoding.UTF8.GetBytes(TextItem));
        var sidecar = MemoryFileReader.Parse(await _repo.ShowAsync(SidecarPath, Ct) + "\n");
        sidecar.Name.Should().Be("Quote 2026");
        sidecar.Description.Should().Be("Roof repair quote");
        sidecar.UnknownKeys["sha256"].Should().Be(TextSha256);
        sidecar.UnknownKeys["size_bytes"].Should().Be("68");
        sidecar.UnknownKeys["media_type"].Should().Be("text/plain");
        sidecar.UnknownKeys["source_name"].Should().Be("quote.txt");
        sidecar.UnknownKeys["archived"].Should().BeOneOf(before, after);
    }

    [Fact]
    public async Task Add_Png_StoredAsPngWithImagePngType()
    {
        // Arrange
        var source = await _repo.WriteSourceAsync("shot.PNG", Png());

        // Act
        var run = await AddAsync(source, name: "Screenshot", description: "Screenshot of the roof");

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr + run.Stdout);
        (await _repo.LastCommitSubjectAsync(Ct)).Should().Be("archive add zyggy/screenshot.png");
        (await _repo.ShowBytesAsync("acme/alice/archive/zyggy/screenshot.png", Ct)).Should().Equal(Png());
        var sidecar = MemoryFileReader.Parse(await _repo.ShowAsync("acme/alice/archive/zyggy/screenshot.md", Ct) + "\n");
        sidecar.UnknownKeys["media_type"].Should().Be("image/png");
        sidecar.UnknownKeys["source_name"].Should().Be("shot.PNG");
    }

    [Fact]
    public async Task Add_SessionUploadUnderClaudeUploads_Archived()
    {
        // Arrange: regression (Central, 2026-10-10) — an image sent in a session lands in ~/.claude/uploads/<session>/.
        var upload = Path.Combine(_repo.HomeDir, ".claude", "uploads", "e80198bf", "c5082833-image.png");
        Directory.CreateDirectory(Path.GetDirectoryName(upload)!);
        await File.WriteAllBytesAsync(upload, Png(), Ct);

        // Act
        var run = await AddAsync(upload, name: "Roof photo", description: "Photo of the roof damage");

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr + run.Stdout);
        (await _repo.LastCommitSubjectAsync(Ct)).Should().Be("archive add zyggy/roof-photo.png");
        MemoryFileReader.Parse(await _repo.ShowAsync("acme/alice/archive/zyggy/roof-photo.md", Ct) + "\n").UnknownKeys["source_name"].Should().Be("c5082833-image.png");
    }

    [Fact]
    public async Task Add_JpegBytesNamedPng_StoredAsJpg()
    {
        // Arrange
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, .. "JFIF\0"u8, .. new byte[9], 0xFF, 0xD9];
        var source = await _repo.WriteSourceAsync("photo.png", jpeg);

        // Act
        var run = await AddAsync(source, name: "Photo", description: "Photo of the gutter");

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr + run.Stdout);
        (await _repo.LastCommitPathsAsync(Ct)).Should().Equal("acme/alice/archive/zyggy/photo.jpg", "acme/alice/archive/zyggy/photo.md");
        MemoryFileReader.Parse(await _repo.ShowAsync("acme/alice/archive/zyggy/photo.md", Ct) + "\n").UnknownKeys["media_type"].Should().Be("image/jpeg");
    }

    [Fact]
    public async Task Add_Pdf_StoredAsPdf()
    {
        // Arrange
        var pdf = "%PDF-1.4\n1 0 obj<<>>endobj\ntrailer<<>>\n%%EOF\n"u8.ToArray();
        var source = await _repo.WriteSourceAsync("invoice.pdf", pdf);

        // Act
        var run = await AddAsync(source, name: "Roof invoice", description: "Invoice for the roof repair");

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr + run.Stdout);
        (await _repo.LastCommitSubjectAsync(Ct)).Should().Be("archive add zyggy/roof-invoice.pdf");
        (await _repo.ShowBytesAsync("acme/alice/archive/zyggy/roof-invoice.pdf", Ct)).Should().Equal(pdf);
        MemoryFileReader.Parse(await _repo.ShowAsync("acme/alice/archive/zyggy/roof-invoice.md", Ct) + "\n").UnknownKeys["media_type"].Should().Be("application/pdf");
    }

    [Fact]
    public async Task Add_MarkdownSource_StoredAsTxtWithTextMarkdownType()
    {
        // Arrange
        var source = await _repo.WriteSourceAsync("notes.md", "# Notes\n\nCall the roofer on Monday.\n"u8.ToArray());

        // Act
        var run = await AddAsync(source, name: "Roof notes", description: "Notes on the roof repair");

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr + run.Stdout);
        (await _repo.LastCommitSubjectAsync(Ct)).Should().Be("archive add zyggy/roof-notes.txt");
        MemoryFileReader.Parse(await _repo.ShowAsync("acme/alice/archive/zyggy/roof-notes.md", Ct) + "\n").UnknownKeys["media_type"].Should().Be("text/markdown");
    }

    [Fact]
    public async Task Add_Text_InboxLineWrittenNotCommittedNotTracked()
    {
        // Arrange
        var source = await TextSourceAsync();

        // Act
        var run = await AddAsync(source);

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr + run.Stdout);
        var line = run.Stdout.Split('\n')[2];
        var date = line["- [stated] ".Length..][..10];
        line.Should().Be($"- [stated] {date} (project:zyggy): Archived \"Quote 2026\" (text/plain, 68 B) at archive/zyggy/quote-2026.txt — Roof repair quote");
        var inbox = Path.Combine(_repo.PrincipalDir, "inbox", $"remember-{date}.md");
        File.ReadAllText(inbox).Should().EndWith("---\n" + line + "\n");
        (await _repo.GitAsync(_repo.CloneDir, ["ls-files", "acme/alice/inbox"], Ct)).Should().BeEmpty();
        (await _repo.LastCommitPathsAsync(Ct)).Should().NotContain(p => p.Contains("/inbox/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Add_Text_StdoutFourLinesInOrder()
    {
        // Arrange
        var source = await TextSourceAsync();

        // Act
        var run = await AddAsync(source);

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr + run.Stdout);
        run.Stderr.Should().BeEmpty();
        var sha = await _repo.GitAsync(_repo.BareDir, ["rev-parse", "main"], Ct);
        var lines = run.Stdout.Split('\n');
        lines.Should().HaveCount(5);
        lines[0].Should().Be("archived: archive/zyggy/quote-2026.txt");
        lines[1].Should().Be("sidecar: archive/zyggy/quote-2026.md");
        lines[2].Should().StartWith("- [stated] ");
        lines[3].Should().Be($"commit: {sha} pushed");
        lines[4].Should().BeEmpty();
    }

    [Fact]
    public async Task Add_ProjectFileMissing_StderrNote()
    {
        // Arrange
        var source = await TextSourceAsync();

        // Act
        var run = await AddAsync(source, project: "house-move");

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr + run.Stdout);
        run.Stderr.Should().Be("archive: note: no memory file named house-move yet; the dream will create it\n");
        (await _repo.LastCommitSubjectAsync(Ct)).Should().Be("archive add house-move/quote-2026.txt");
    }

    [Fact]
    public async Task Add_SameBytesTwiceWithNewSlug_TwoItemsSameSha()
    {
        // Arrange
        var source = await TextSourceAsync();
        var first = await AddAsync(source);

        // Act
        var second = await AddAsync(source, extra: ["--slug", "quote-2026-copy"]);

        // Assert
        first.ExitCode.Should().Be(0, first.Stderr);
        second.ExitCode.Should().Be(0, second.Stderr);
        var copy = MemoryFileReader.Parse(await _repo.ShowAsync("acme/alice/archive/zyggy/quote-2026-copy.md", Ct) + "\n");
        var original = MemoryFileReader.Parse(await _repo.ShowAsync(SidecarPath, Ct) + "\n");
        copy.UnknownKeys["sha256"].Should().Be(original.UnknownKeys["sha256"]).And.Be(TextSha256);
        Convert.ToHexStringLower(SHA256.HashData(await _repo.ShowBytesAsync("acme/alice/archive/zyggy/quote-2026-copy.txt", Ct))).Should().Be(TextSha256);
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "Unix file modes: Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task Add_OnLinux_ItemFileModeIsNotExecutable()
    {
        // Arrange
        var source = await TextSourceAsync();
        File.SetUnixFileMode(source, File.GetUnixFileMode(source) | UnixFileMode.UserExecute);

        // Act
        var run = await AddAsync(source);

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr + run.Stdout);
        var mode = File.GetUnixFileMode(Path.Combine(_repo.PrincipalDir, "archive", "zyggy", "quote-2026.txt"));
        (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)).Should().Be(UnixFileMode.None);
    }
}
