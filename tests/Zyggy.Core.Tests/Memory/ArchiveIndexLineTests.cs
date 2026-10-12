using Zyggy.Core.Memory;

namespace Zyggy.Core.Tests.Memory;

public sealed class ArchiveIndexLineTests
{
    private static readonly ArchiveAddRequest Request = new(
        Slug.Parse("zyggy"),
        "Quote 2026",
        "Roof repair quote, valid 30 days",
        Path.Combine(Path.GetTempPath(), "quote.txt"),
        Slug.Parse("quote-2026"));

    [Fact]
    public void Added_FixedInputs_EqualsSpecLine()
    {
        // Act
        var line = ArchiveIndexLine.Added(new DateOnly(2026, 9, 30), Request, ArchiveMediaType.TextPlain, 66);

        // Assert
        line.Should().Be("- [stated] 2026-09-30 (project:zyggy): Archived \"Quote 2026\" (text/plain, 66 B) at archive/zyggy/quote-2026.txt — Roof repair quote, valid 30 days");
    }

    [Theory]
    [InlineData(ArchiveMediaType.Docx, "Roof offer", "roof-offer", "Contractor offer for the roof repair", 1289,
        "- [stated] 2026-09-30 (project:zyggy): Archived \"Roof offer\" (Word document, 1.3 KB) at archive/zyggy/roof-offer.docx — Contractor offer for the roof repair")]
    [InlineData(ArchiveMediaType.Xlsx, "Roof budget", "roof-budget", "Cost lines for the roof repair", 2262,
        "- [stated] 2026-09-30 (project:zyggy): Archived \"Roof budget\" (Excel workbook, 2.2 KB) at archive/zyggy/roof-budget.xlsx — Cost lines for the roof repair")]
    [InlineData(ArchiveMediaType.Pptx, "Roof pitch", "roof-pitch", "Slides shown to the contractor", 1335,
        "- [stated] 2026-09-30 (project:zyggy): Archived \"Roof pitch\" (PowerPoint deck, 1.3 KB) at archive/zyggy/roof-pitch.pptx — Slides shown to the contractor")]
    public void Added_Office_UsesShortLabel(ArchiveMediaType type, string name, string slug, string description, long size, string expected)
    {
        // Arrange
        var request = new ArchiveAddRequest(Slug.Parse("zyggy"), name, description, Path.Combine(Path.GetTempPath(), "sample.bin"), Slug.Parse(slug));

        // Act
        var line = ArchiveIndexLine.Added(new DateOnly(2026, 9, 30), request, type, size);

        // Assert
        line.Should().Be(expected);
    }

    [Theory]
    [InlineData(ArchiveMediaType.TextPlain, "text/plain")]
    [InlineData(ArchiveMediaType.TextMarkdown, "text/markdown")]
    [InlineData(ArchiveMediaType.Pdf, "application/pdf")]
    [InlineData(ArchiveMediaType.Png, "image/png")]
    [InlineData(ArchiveMediaType.Jpeg, "image/jpeg")]
    [InlineData(ArchiveMediaType.Gif, "image/gif")]
    public void Added_ExistingTypes_ByteIdentical(ArchiveMediaType type, string wire)
    {
        // Act
        var line = ArchiveIndexLine.Added(new DateOnly(2026, 9, 30), Request, type, 66);

        // Assert: the 37 form — the wire string, never a label.
        line.Should().Be($"- [stated] 2026-09-30 (project:zyggy): Archived \"Quote 2026\" ({wire}, 66 B) at archive/zyggy/quote-2026.{ArchiveMediaTypeWire.Extension(type)} — Roof repair quote, valid 30 days");
    }

    [Fact]
    public void Longest_UnchangedForEveryRequest()
    {
        // Act
        var longest = ArchiveIndexLine.Longest(new DateOnly(2026, 9, 30), Request);

        // Assert: the pre-39 string, byte for byte (AC-4).
        longest.Should().Be("- [stated] 2026-09-30 (project:zyggy): Archived \"Quote 2026\" (application/pdf, 999.9 MB) at archive/zyggy/quote-2026.pdf — Roof repair quote, valid 30 days");
    }

    [Fact]
    public void Added_PptxVersusLongest_AtMostOneCharacterLonger()
    {
        // Act: plan assumption A2 — `PowerPoint deck` + `pptx` is 19 variable characters against `application/pdf` + `pdf` = 18.
        var longest = ArchiveIndexLine.Longest(new DateOnly(2026, 9, 30), Request);
        var pptx = ArchiveIndexLine.Added(new DateOnly(2026, 9, 30), Request, ArchiveMediaType.Pptx, 1_047_000_000);

        // Assert
        (pptx.Length - longest.Length).Should().Be(1);
    }

    [Fact]
    public void Removed_FixedInputs_EqualsSpecLine()
    {
        // Act
        var line = ArchiveIndexLine.Removed(new DateOnly(2026, 9, 30), Slug.Parse("zyggy"), Slug.Parse("quote-2026"), "txt", "Quote 2026");

        // Assert
        line.Should().Be("- [stated] 2026-09-30 (project:zyggy): Removed archived item archive/zyggy/quote-2026.txt (\"Quote 2026\")");
    }

    [Fact]
    public void Longest_UsesLongestTypeAndSizePlaceholder()
    {
        // Act
        var longest = ArchiveIndexLine.Longest(new DateOnly(2026, 9, 30), Request);
        var real = ArchiveIndexLine.Added(new DateOnly(2026, 9, 30), Request, ArchiveMediaType.Pdf, 10_485_760);

        // Assert
        longest.Length.Should().BeGreaterThanOrEqualTo(real.Length);
        longest.Should().Contain("(application/pdf, 999.9 MB)");
        ArchiveIndexLine.MaxLength.Should().Be(400);
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(66, "66 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1_048_576, "1.0 MB")]
    [InlineData(10_485_760, "10.0 MB")]
    public void Size_Format_Rows(long bytes, string expected)
    {
        // Act
        var actual = ArchiveSize.Format(bytes);

        // Assert
        actual.Should().Be(expected);
    }
}
