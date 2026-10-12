using System.Text;

using Zyggy.Core.Memory;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Core.Tests.LinkedIn;
using Zyggy.Core.Tests.Media;

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
    public void Check_ZipHeaderOnly_OfficeRefusedZipMalformed()
    {
        // Act: spec 39 — a ZIP signature without an archive behind it is an Office-shaped file that cannot be opened (the 37 deviation).
        var result = Check(Request(Write(Path.Combine(_sources, "report.docx"), [0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x06, 0x00])));

        // Assert
        result.Refusal.Should().Be(ArchiveRefusal.OfficeRefused);
        result.Detail.Should().Be("zip_malformed");
    }

    [Theory]
    [InlineData("sample.docx", ArchiveMediaType.Docx, "fa0d4998a5b647d23edf5ec41eaa6a1192341017c46d5f465a4208fc5345e0ca")]
    [InlineData("sample.xlsx", ArchiveMediaType.Xlsx, "3c6625560fa0b5b60c3c704353460a841f34938af9701bc423f4ac0cb1ffa2f9")]
    [InlineData("sample.pptx", ArchiveMediaType.Pptx, "aa100f3a2150a877f29c06db427e70bd53e65759d9e80c1e493bd90a7a4e25b5")]
    public void Check_CommittedOffice_CandidateTypeAndSha256Hand(string sample, ArchiveMediaType type, string sha256)
    {
        // Act: the digests are those of tests/golden/README.md (sha256sum), never the code's.
        var result = Check(Request(Write(Path.Combine(_sources, sample), Sample(sample))));

        // Assert
        result.Refusal.Should().BeNull();
        result.Candidate!.Type.Should().Be(type);
        result.Candidate.Sha256.Should().Be(sha256);
        result.Candidate.SourceName.Should().Be(sample);
    }

    [Fact]
    public void Check_DocxRenamedZip_CandidateDocxSourceNameKept()
    {
        // Act
        var result = Check(Request(Write(Path.Combine(_sources, "report.zip"), Sample("sample.docx"))));

        // Assert
        result.Candidate!.Type.Should().Be(ArchiveMediaType.Docx);
        result.Candidate.SourceName.Should().Be("report.zip");
    }

    [Fact]
    public void Check_DocxRenamedPdf_CandidateDocx()
    {
        // Act
        var result = Check(Request(Write(Path.Combine(_sources, "report.pdf"), Sample("sample.docx"))));

        // Assert
        result.Candidate!.Type.Should().Be(ArchiveMediaType.Docx);
    }

    [Fact]
    public void Check_DocmRenamedDocx_OfficeRefusedMacroEnabled()
    {
        // Act
        var result = Check(Request(Write(Path.Combine(_sources, "report.docx"), Sample("sample.docm"))));

        // Assert
        result.Refusal.Should().Be(ArchiveRefusal.OfficeRefused);
        result.Detail.Should().Be("macro_enabled");
    }

    [Theory]
    [InlineData("macro_enabled")]
    [InlineData("active_content")]
    [InlineData("legacy_binary")]
    [InlineData("encrypted")]
    [InlineData("opendocument")]
    [InlineData("office_variant")]
    [InlineData("not_office")]
    [InlineData("zip_malformed")]
    [InlineData("zip_bounds")]
    public void Check_OfficeRefusal_DetailIsTheClosedWord(string detail)
    {
        // Arrange: one sample per detail.
        byte[] bytes = detail switch
        {
            "macro_enabled" => Sample("sample.docm"),
            "active_content" => OfficeBytes.Docx(OfficeBytes.Stored("word/vbaProject.bin", new byte[8])),
            "legacy_binary" => Sample("sample.doc"),
            "encrypted" => OfficeBytes.Cfb(true),
            "opendocument" => Sample("sample.odt"),
            "office_variant" => OfficeBytes.WithMainType("application/vnd.openxmlformats-officedocument.wordprocessingml.template.main+xml"),
            "not_office" => Sample("plain.zip"),
            "zip_malformed" => [0x50, 0x4B, 0x03, 0x04, 1, 2, 3, 4, 5, 6, 7, 8],
            "zip_bounds" => OfficeBytes.Zip(Enumerable.Range(0, 10_001).Select(i => OfficeBytes.Stored($"e{i}", ""))),
            _ => throw new ArgumentOutOfRangeException(nameof(detail)),
        };

        // Act
        var result = Check(Request(Write(Path.Combine(_sources, "handed-over.docx"), bytes)));

        // Assert
        result.Refusal.Should().Be(ArchiveRefusal.OfficeRefused);
        result.Detail.Should().Be(detail);
        result.Detail.Should().NotContainAny("/", ".xml", "+");
    }

    [Fact]
    public void Check_DocxNotInAllowList_TypeNotAllowedNamesWire()
    {
        // Arrange: the six 37 members only — the instance that has not opted in.
        var options = new ArchiveOptions
        {
            AllowedTypes = new HashSet<ArchiveMediaType>
            {
                ArchiveMediaType.TextPlain, ArchiveMediaType.TextMarkdown, ArchiveMediaType.Pdf, ArchiveMediaType.Png, ArchiveMediaType.Jpeg, ArchiveMediaType.Gif,
            },
        };

        // Act
        var result = Check(Request(Write(Path.Combine(_sources, "sample.docx"), Sample("sample.docx"))), options);

        // Assert
        result.Refusal.Should().Be(ArchiveRefusal.TypeNotAllowed);
        result.Detail.Should().Be("application/vnd.openxmlformats-officedocument.wordprocessingml.document");
    }

    [Fact]
    public void Check_DocxWithSecretShapedText_AcceptedNotScanned()
    {
        // Arrange: AC-11 — the AWS sample of secret-samples.txt inside word/document.xml; Office content is not scanned (OQ-1 (b)).
        var document = Encoding.UTF8.GetBytes("<w:document xmlns:w=\"x\"><w:body><w:p><w:r><w:t>key AKIAABCDEFGHIJKLMNOP</w:t></w:r></w:p></w:body></w:document>");
        var bytes = OfficeBytes.Package(
            OfficeBytes.ContentTypes([("/word/document.xml", OfficeBytes.DocxMain)]),
            OfficeBytes.Stored("_rels/.rels", OfficeBytes.Rels("word/document.xml")),
            OfficeBytes.Stored("word/document.xml", document));

        // Act
        var result = Check(Request(Write(Path.Combine(_sources, "notes.docx"), bytes)));

        // Assert
        result.Refusal.Should().BeNull();
        result.Detail.Should().BeNull();
        result.Candidate!.Type.Should().Be(ArchiveMediaType.Docx);
    }

    [Fact]
    public void Check_DocxNameWithSecret_SecretPatternName()
    {
        // Act: the name and description scans still apply to an Office item.
        var result = Check(Request(Write(Path.Combine(_sources, "sample.docx"), Sample("sample.docx"))) with { Name = "key AKIAABCDEFGHIJKLMNOP" });

        // Assert
        result.Refusal.Should().Be(ArchiveRefusal.SecretPattern);
        result.Detail.Should().Be("aws-access-key (name)");
    }

    [Fact]
    public void Check_OfficeOrder_SourceBeforeTypeBeforeSize()
    {
        // Arrange
        var deniedDocm = Write(Path.Combine(_home, ".ssh", "macros.docm"), Sample("sample.docm"));
        var docx = Write(Path.Combine(_sources, "sample.docx"), Sample("sample.docx"));

        // Act
        var denied = Check(Request(deniedDocm));
        var tooLarge = Check(Request(docx), new ArchiveOptions { ItemMaxBytes = 1000, ProjectMaxBytes = 2000, TotalMaxBytes = 3000 });

        // Assert
        denied.Refusal.Should().Be(ArchiveRefusal.SourceRefused);
        tooLarge.Refusal.Should().Be(ArchiveRefusal.TooLarge);
        tooLarge.Detail.Should().Be("1289 > 1000");
    }

    [Fact]
    public void Check_PptxLineExactlyOneOver400_UsageBeforeAnyWrite()
    {
        // Arrange: plan assumption A2 — Longest is exactly 400 for this request, but a 100 MB PowerPoint deck composes 401 characters.
        var request = RequestWithLongestOf400(Write(Path.Combine(_sources, "deck.pptx"), HundredMegabytePptx()));
        var options = new ArchiveOptions { ItemMaxBytes = 200_000_000, ProjectMaxBytes = 300_000_000, TotalMaxBytes = 400_000_000 };

        // Act
        var result = Check(request, options);

        // Assert
        ArchiveIndexLine.Longest(Today, request).Length.Should().Be(400);
        result.Refusal.Should().BeNull($"{result.Refusal}: {result.Detail}");
        result.UsageError.Should().Be("the index line would exceed 400 characters (401): shorten --name or --description");
        result.Refusal.Should().BeNull();
        result.Candidate.Should().BeNull();
    }

    [Fact]
    public void Check_PptxLineExactly400_Candidate()
    {
        // Arrange: the same request with the committed 1335-byte deck — "1.3 KB" is two characters shorter than the placeholder.
        var request = RequestWithLongestOf400(Write(Path.Combine(_sources, "deck.pptx"), Sample("sample.pptx")));

        // Act
        var result = Check(request);

        // Assert
        result.Refusal.Should().BeNull($"{result.Refusal}: {result.Detail}");
        result.UsageError.Should().BeNull();
        result.Candidate!.IndexLine.Length.Should().Be(399);
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
            ArchiveRefusal.ContactDetail, ArchiveRefusal.SlugTaken, ArchiveRefusal.OfficeRefused,
        };
        Enum.GetValues<ArchiveRefusal>().Except(produced).Should().BeEquivalentTo([ArchiveRefusal.Unattended, ArchiveRefusal.NotFound]);
    }

    private static ArchiveAddRequest Request(string source) =>
        new(Slug.Parse("zyggy"), "Quote 2026", "Roof repair quote", source, Slug.Parse("quote-2026"));

    // A pptx request whose description pads ArchiveIndexLine.Longest to exactly 400 characters.
    private static ArchiveAddRequest RequestWithLongestOf400(string source)
    {
        var bare = new ArchiveAddRequest(Slug.Parse("zyggy"), "Roof pitch", string.Empty, source, Slug.Parse("roof-pitch"));
        var pad = 400 - ArchiveIndexLine.Longest(Today, bare).Length;
        return bare with { Description = Words(pad) };
    }

    // A description of the given length that no secret pattern matches: runs of nine letters separated by spaces, never ending in one.
    private static string Words(int length) =>
        string.Create(length, 0, (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = i % 10 == 9 && i != span.Length - 1 ? ' ' : 'x';
            }
        });

    private static byte[] Sample(string name) => File.ReadAllBytes(Path.Combine(Golden.Directory, "office", name));

    // A valid pptx of at least 100 MB (size prints as "100.0 MB", the placeholder's eight characters): one stored part of zeros.
    private static byte[] HundredMegabytePptx() => OfficeBytes.Pptx(OfficeBytes.Stored("ppt/media/image1.bin", new byte[104_857_600]));

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
