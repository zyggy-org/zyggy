using System.Text;

using Zyggy.Core.Memory;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Core.Tests.LinkedIn;

namespace Zyggy.Core.Tests.Memory;

/// <summary>
/// The closed checks of <c>archive add</c> (spec 37 AC-7..AC-11) over a temp source folder and a temp memory tree: one producing test per
/// <see cref="ArchiveRefusal"/> member a check can produce, the bytes read once, the detail never carrying item text.
/// </summary>
public sealed class ArchiveChecksTests : IDisposable
{
    private const string TextItem = "Quote for the Zyggy roof repair.\nTotal 1 234,00 EUR, valid 30 days.\n";

    private const string TextSha256 = "c5f2cab5d3b4d46c44bee6f1ee037d471c419b739908f4a05251745fb4794f88";

    private static readonly DateOnly Today = new(2026, 9, 30);

    private readonly MemoryTree _tree = new();
    private readonly string _home;
    private readonly string _sources;

    public ArchiveChecksTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "zyggy-ut-checks", Guid.NewGuid().ToString("N"));
        _sources = Path.Combine(_home, "Downloads");
        Directory.CreateDirectory(_sources);
    }

    public static bool IsLinux => OperatingSystem.IsLinux();

    public void Dispose()
    {
        _tree.Dispose();
        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, recursive: true);
        }
    }

    [Fact]
    public void Check_MissingSource_SourceRefusedNotFound()
    {
        // Act
        var result = Check(Request(Path.Combine(_sources, "missing.txt")));

        // Assert
        result.Refusal.Should().Be(ArchiveRefusal.SourceRefused);
        result.Detail.Should().Be("not_found");
    }

    [Fact]
    public void Check_Directory_SourceRefusedNotRegularFile()
    {
        // Act
        var result = Check(Request(_sources));

        // Assert
        result.Refusal.Should().Be(ArchiveRefusal.SourceRefused);
        result.Detail.Should().Be("not_regular_file");
    }

    [Fact]
    public void Check_SourceInsideMemory_SourceRefusedInsideMemory()
    {
        // Arrange
        _tree.Write("archive/zyggy/old.txt", TextItem);

        // Act
        var result = Check(Request(_tree.Full("archive/zyggy/old.txt")));

        // Assert
        result.Refusal.Should().Be(ArchiveRefusal.SourceRefused);
        result.Detail.Should().Be("inside_memory");
    }

    [Fact]
    public void Check_SourceUnderDeniedLocation_SourceRefusedDeniedLocation()
    {
        // Arrange
        var path = Write(Path.Combine(_home, ".ssh", "id_ed25519"), "not really a key\n"u8.ToArray());

        // Act
        var result = Check(Request(path));

        // Assert
        result.Refusal.Should().Be(ArchiveRefusal.SourceRefused);
        result.Detail.Should().Be("denied_location");
    }

    [Fact]
    public void Check_SourceUnderInstanceDeny_SourceRefusedDeniedLocation()
    {
        // Arrange
        var secret = Path.Combine(_home, "Documents", "secret");
        var path = Write(Path.Combine(secret, "x.txt"), TextItem);

        // Act
        var result = Check(Request(path), new ArchiveOptions { SourceDeny = [secret] });

        // Assert
        result.Refusal.Should().Be(ArchiveRefusal.SourceRefused);
        result.Detail.Should().Be("denied_location");
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "symbolic links without privileges: Linux only")]
    public void Check_SymlinkSource_OnLinux_SourceRefusedSymlink()
    {
        // Arrange
        var real = Write(Path.Combine(_home, "real", "quote.txt"), TextItem);
        var fileLink = Path.Combine(_sources, "quote-link.txt");
        File.CreateSymbolicLink(fileLink, real);
        var dirLink = Path.Combine(_sources, "linked-dir");
        Directory.CreateSymbolicLink(dirLink, Path.Combine(_home, "real"));

        // Act
        var throughFile = Check(Request(fileLink));
        var throughDirectory = Check(Request(Path.Combine(dirLink, "quote.txt")));

        // Assert
        throughFile.Refusal.Should().Be(ArchiveRefusal.SourceRefused);
        throughFile.Detail.Should().Be("symlink");
        throughDirectory.Refusal.Should().Be(ArchiveRefusal.SourceRefused);
        throughDirectory.Detail.Should().Be("symlink");
    }

    [Fact]
    public void Check_EmptyFile_Empty()
    {
        // Act
        var result = Check(Request(Write(Path.Combine(_sources, "empty.txt"), [])));

        // Assert
        result.Refusal.Should().Be(ArchiveRefusal.Empty);
    }

    [Fact]
    public void Check_UnknownBytes_TypeRefused()
    {
        // Act
        var result = Check(Request(Write(Path.Combine(_sources, "blob.bin"), [0x00, 0x01, 0x02, 0xFE, 0xFF])));

        // Assert
        result.Refusal.Should().Be(ArchiveRefusal.TypeRefused);
        result.Detail.Should().BeNull();
    }

    [Fact]
    public void Check_DocxBytes_TypeRefused()
    {
        // Act
        var result = Check(Request(Write(Path.Combine(_sources, "report.docx"), [0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x06, 0x00])));

        // Assert
        result.Refusal.Should().Be(ArchiveRefusal.TypeRefused);
    }

    [Fact]
    public void Check_TypeOutsideAllowList_TypeNotAllowedNamesType()
    {
        // Arrange
        var options = new ArchiveOptions { AllowedTypes = new HashSet<ArchiveMediaType> { ArchiveMediaType.TextPlain } };

        // Act
        var result = Check(Request(Write(Path.Combine(_sources, "pic.png"), ImageBytes.Png(2, 2))), options);

        // Assert
        result.Refusal.Should().Be(ArchiveRefusal.TypeNotAllowed);
        result.Detail.Should().Be("image/png");
    }

    [Fact]
    public void Check_OverItemMax_TooLargeDetailNumbers()
    {
        // Act
        var result = Check(Request(Write(Path.Combine(_sources, "quote.txt"), TextItem)), new ArchiveOptions { ItemMaxBytes = 10, ProjectMaxBytes = 100, TotalMaxBytes = 1000 });

        // Assert
        result.Refusal.Should().Be(ArchiveRefusal.TooLarge);
        result.Detail.Should().Be("68 > 10");
    }

    [Fact]
    public void Check_ProjectCapExceeded_ProjectCap()
    {
        // Arrange: an existing item (40 bytes) and its sidecar (60 bytes) count toward the project's bytes (assumption A3).
        _tree.Write("archive/zyggy/old.txt", new string('x', 40));
        _tree.Write("archive/zyggy/old.md", new string('y', 60));
        var options = new ArchiveOptions { ItemMaxBytes = 100, ProjectMaxBytes = 150, TotalMaxBytes = 1000 };

        // Act
        var result = Check(Request(Write(Path.Combine(_sources, "quote.txt"), TextItem)), options);

        // Assert
        result.Refusal.Should().Be(ArchiveRefusal.ProjectCap);
    }

    [Fact]
    public void Check_TotalCapExceeded_TotalCap()
    {
        // Arrange: two projects of 400 bytes each; the new 68-byte item breaks the 800-byte total but not the 1000-byte project cap.
        _tree.Write("archive/zyggy/old.txt", new string('x', 400));
        _tree.Write("archive/other/old.txt", new string('x', 400));
        var options = new ArchiveOptions { ItemMaxBytes = 100, ProjectMaxBytes = 800, TotalMaxBytes = 800 };

        // Act
        var result = Check(Request(Write(Path.Combine(_sources, "quote.txt"), TextItem)), options);

        // Assert
        result.Refusal.Should().Be(ArchiveRefusal.TotalCap);
    }

    [Fact]
    public void Check_TextWithSecretLine_SecretPatternNamesPatternAndLineNeverText()
    {
        // Arrange
        const string Sample = "AKIAABCDEFGHIJKLMNOP";
        var path = Write(Path.Combine(_sources, "notes.txt"), $"Meeting notes.\nkey {Sample}\nNext steps.\n");

        // Act
        var result = Check(Request(path));

        // Assert
        result.Refusal.Should().Be(ArchiveRefusal.SecretPattern);
        result.Detail.Should().Be("aws-access-key (line 2)");
        result.Detail.Should().NotContainAny(Sample, "Meeting", "Next steps");
    }

    [Fact]
    public void Check_TextWithEmailAndPhone_Accepted()
    {
        // Arrange: owner decision OQ-1 (b) — contact details inside the item are allowed.
        var path = Write(Path.Combine(_sources, "contacts.txt"), "Call Marie at +32 470 12 34 56 or marie@example.com\n");

        // Act
        var result = Check(Request(path));

        // Assert
        result.Refusal.Should().BeNull();
        result.Candidate.Should().NotBeNull();
    }

    [Fact]
    public void Check_NameWithSecret_SecretPatternName()
    {
        // Act
        var result = Check(Request(Write(Path.Combine(_sources, "quote.txt"), TextItem)) with { Name = "key AKIAABCDEFGHIJKLMNOP" });

        // Assert
        result.Refusal.Should().Be(ArchiveRefusal.SecretPattern);
        result.Detail.Should().Be("aws-access-key (name)");
    }

    [Fact]
    public void Check_DescriptionWithSecret_SecretPatternDescription()
    {
        // Act
        var result = Check(Request(Write(Path.Combine(_sources, "quote.txt"), TextItem)) with { Description = "token ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789" });

        // Assert
        result.Refusal.Should().Be(ArchiveRefusal.SecretPattern);
        result.Detail.Should().Be("github-token (description)");
    }

    [Fact]
    public void Check_NameWithEmail_ContactDetailName()
    {
        // Act
        var result = Check(Request(Write(Path.Combine(_sources, "quote.txt"), TextItem)) with { Name = "Quote from marie@example.com" });

        // Assert
        result.Refusal.Should().Be(ArchiveRefusal.ContactDetail);
        result.Detail.Should().Be("name");
    }

    [Fact]
    public void Check_DescriptionWithPhone_ContactDetailDescription()
    {
        // Act
        var result = Check(Request(Write(Path.Combine(_sources, "quote.txt"), TextItem)) with { Description = "Call +32 470 12 34 56 for details" });

        // Assert
        result.Refusal.Should().Be(ArchiveRefusal.ContactDetail);
        result.Detail.Should().Be("description");
    }

    [Fact]
    public void Check_BinaryWithSecretLookingBytes_NotScanned()
    {
        // Arrange: a PNG whose padding carries a secret-shaped text is accepted — binary items are not content-scanned (spec Risks).
        var png = ImageBytes.Png(2, 2, pad: 64);
        "key AKIAABCDEFGHIJKLMNOP"u8.CopyTo(png.AsSpan(40));

        // Act
        var result = Check(Request(Write(Path.Combine(_sources, "shot.png"), png)));

        // Assert
        result.Refusal.Should().BeNull();
        result.Candidate!.Type.Should().Be(ArchiveMediaType.Png);
    }

    [Fact]
    public void Check_SidecarExists_SlugTaken()
    {
        // Arrange
        _tree.Write("archive/zyggy/quote-2026.md", "---\nname: Quote 2026\n---\n");

        // Act
        var result = Check(Request(Write(Path.Combine(_sources, "quote.txt"), TextItem)));

        // Assert
        result.Refusal.Should().Be(ArchiveRefusal.SlugTaken);
    }

    [Fact]
    public void Check_ItemWithOtherExtensionExists_SlugTaken()
    {
        // Arrange
        _tree.Write("archive/zyggy/quote-2026.pdf", "%PDF-1.4\n");

        // Act
        var result = Check(Request(Write(Path.Combine(_sources, "quote.txt"), TextItem)));

        // Assert
        result.Refusal.Should().Be(ArchiveRefusal.SlugTaken);
    }

    [Fact]
    public void Check_PngNamedTxt_CandidateIsImagePngSourceNameKept()
    {
        // Act
        var result = Check(Request(Write(Path.Combine(_sources, "picture.txt"), ImageBytes.Png(2, 2))));

        // Assert
        result.Refusal.Should().BeNull();
        result.Candidate!.Type.Should().Be(ArchiveMediaType.Png);
        result.Candidate.SourceName.Should().Be("picture.txt");
    }

    [Fact]
    public void Check_MarkdownFile_TextMarkdownCandidate()
    {
        // Act
        var result = Check(Request(Write(Path.Combine(_sources, "notes.md"), "# Notes\n\nA line.\n")));

        // Assert
        result.Candidate!.Type.Should().Be(ArchiveMediaType.TextMarkdown);
    }

    [Fact]
    public void Check_TextWithBom_TextPlainBytesUnchanged()
    {
        // Arrange
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(TextItem)];

        // Act
        var result = Check(Request(Write(Path.Combine(_sources, "bom.txt"), bytes)));

        // Assert
        result.Candidate!.Type.Should().Be(ArchiveMediaType.TextPlain);
        result.Candidate.Bytes.Should().Equal(bytes);
    }

    [Fact]
    public void Check_Valid_CandidateHasSha256OfExactBytes()
    {
        // Act
        var result = Check(Request(Write(Path.Combine(_sources, "quote.txt"), TextItem)));

        // Assert
        result.Refusal.Should().BeNull();
        result.Candidate!.Sha256.Should().Be(TextSha256);
        result.Candidate.Bytes.Should().HaveCount(68);
        result.Candidate.IndexLine.Should().Be(
            "- [stated] 2026-09-30 (project:zyggy): Archived \"Quote 2026\" (text/plain, 68 B) at archive/zyggy/quote-2026.txt — Roof repair quote");
    }

    [Fact]
    public void Check_Order_SourceBeforeTypeBeforeSizeBeforeSecretBeforeSlug()
    {
        // Arrange
        var big = Write(Path.Combine(_sources, "big.txt"), "key AKIAABCDEFGHIJKLMNOP\n" + new string('x', 200) + "\n");
        var emptyDenied = Write(Path.Combine(_home, ".claude", "empty.txt"), []);

        // Act
        var tooLargeAndSecret = Check(Request(big), new ArchiveOptions { ItemMaxBytes = 50, ProjectMaxBytes = 100, TotalMaxBytes = 1000 });
        var deniedAndEmpty = Check(Request(emptyDenied));

        // Assert
        tooLargeAndSecret.Refusal.Should().Be(ArchiveRefusal.TooLarge);
        deniedAndEmpty.Refusal.Should().Be(ArchiveRefusal.SourceRefused);
    }

    [Fact]
    public void EveryRefusal_HasAProducingTest()
    {
        // Assert: every member except Unattended (the verb) and NotFound (remove) is produced by a test above.
        var produced = new[]
        {
            ArchiveRefusal.SourceRefused, ArchiveRefusal.TypeRefused, ArchiveRefusal.TypeNotAllowed, ArchiveRefusal.Empty,
            ArchiveRefusal.TooLarge, ArchiveRefusal.ProjectCap, ArchiveRefusal.TotalCap, ArchiveRefusal.SecretPattern,
            ArchiveRefusal.ContactDetail, ArchiveRefusal.SlugTaken,
        };
        Enum.GetValues<ArchiveRefusal>().Except(produced).Should().BeEquivalentTo([ArchiveRefusal.Unattended, ArchiveRefusal.NotFound]);
    }

    private static ArchiveAddRequest Request(string source) =>
        new(Slug.Parse("zyggy"), "Quote 2026", "Roof repair quote", source, Slug.Parse("quote-2026"));

    private static string Write(string path, string text) => Write(path, new UTF8Encoding(false).GetBytes(text));

    private static string Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private ArchiveCheckResult Check(ArchiveAddRequest request, ArchiveOptions? options = null)
    {
        options ??= new ArchiveOptions();
        var env = new Dictionary<string, string?>(StringComparer.Ordinal) { ["HOME"] = _home };
        var deny = SourceDenyList.Build(env, options, _tree.Paths);
        var patterns = SecretPatterns.Load(Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt")).Patterns!;
        return ArchiveChecks.Check(request, options, _tree.Paths, deny, patterns, Today);
    }
}
