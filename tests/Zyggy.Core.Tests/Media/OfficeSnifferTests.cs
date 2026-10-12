using System.Diagnostics;
using System.Text;

using Zyggy.Core.Media;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Media;

/// <summary>
/// Spec 39 AC-5..AC-9: the sniffer tells a Word, Excel or PowerPoint document from every other ZIP or OLE file by its content alone,
/// names why it refused with one of nine closed details, extracts nothing, decompresses at most two small entries and refuses hostile
/// archives within bounds.
/// </summary>
public sealed class OfficeSnifferTests
{
    private const string Vba = "application/vnd.ms-office.vbaProject";

    public static TheoryData<string> MacroEnabledMainTypes() => new()
    {
        "application/vnd.ms-word.document.macroEnabled.main+xml",
        "application/vnd.ms-word.template.macroEnabledTemplate.main+xml",
        "application/vnd.ms-excel.sheet.macroEnabled.main+xml",
        "application/vnd.ms-excel.template.macroEnabled.main+xml",
        "application/vnd.ms-excel.addin.macroEnabled.main+xml",
        "application/vnd.ms-excel.sheet.binary.macroEnabled.main",
        "application/vnd.ms-powerpoint.presentation.macroEnabled.main+xml",
        "application/vnd.ms-powerpoint.slideshow.macroEnabled.main+xml",
        "application/vnd.ms-powerpoint.template.macroEnabled.main+xml",
    };

    public static TheoryData<string> ActiveContentTypes() => new()
    {
        "application/vnd.ms-office.vbaProject",
        "application/vnd.ms-office.vbaProjectSignature",
        "application/vnd.ms-office.vbaProjectSignatureAgile",
        "application/vnd.ms-office.vbaProjectSignatureV3",
        "application/vnd.ms-word.vbaData+xml",
        "application/vnd.ms-office.activeX+xml",
        "application/vnd.ms-office.activeX",
        "application/vnd.ms-excel.macrosheet+xml",
        "application/vnd.ms-excel.intlmacrosheet+xml",
        "application/vnd.ms-word.keyMapCustomizations+xml",
        "application/vnd.ms-word.attachedToolbars",
    };

    public static TheoryData<string> VariantMainTypes() => new()
    {
        "application/vnd.openxmlformats-officedocument.wordprocessingml.template.main+xml",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.template.main+xml",
        "application/vnd.openxmlformats-officedocument.presentationml.template.main+xml",
        "application/vnd.openxmlformats-officedocument.presentationml.slideshow.main+xml",
    };

    public static TheoryData<string> UnsafeNames() => new()
    {
        "/word/document.xml", "word\\document.xml", "../x.xml", "word/./x.xml", "C:x.xml", "word/a\u0007.xml",
    };

    public static TheoryData<string, byte[]> EverySample()
    {
        var data = new TheoryData<string, byte[]>
        {
            { "docx", OfficeBytes.Docx() },
            { "xlsx", OfficeBytes.Xlsx() },
            { "pptx", OfficeBytes.Pptx() },
            { "docm", OfficeBytes.WithMainType(MacroEnabledMainTypes().First().Data) },
            { "vba", OfficeBytes.Docx(OfficeBytes.Stored("word/vbaProject.bin", new byte[8])) },
            { "cfb", OfficeBytes.Cfb(false) },
            { "cfb-encrypted", OfficeBytes.Cfb(true) },
            { "odt", OfficeBytes.Odf("application/vnd.oasis.opendocument.text") },
            { "dotx", OfficeBytes.WithMainType(VariantMainTypes().First().Data) },
            { "plain", OfficeBytes.Zip(OfficeBytes.Stored("readme.txt", "hi")) },
            { "garbage", [0x50, 0x4B, 0x03, 0x04, 1, 2, 3, 4, 5, 6, 7, 8] },
            { "bounds", OfficeBytes.Zip(Enumerable.Range(0, OfficeSniffer.MaxEntries + 1).Select(i => OfficeBytes.Stored($"e{i}", ""))) },
        };
        foreach (var file in new[] { "sample.docx", "sample.xlsx", "sample.pptx", "sample.docm", "sample.odt", "plain.zip" })
        {
            data.Add(file, Sample(file));
        }

        return data;
    }

    private static byte[] Sample(string name) => File.ReadAllBytes(Path.Combine(Golden.Directory, "office", name));

    private static OfficeSniff Sniff(byte[] bytes) => OfficeSniffer.IsCompoundFile(bytes) ? OfficeSniffer.SniffCompoundFile(bytes) : OfficeSniffer.SniffZip(bytes);

    // ---- accepted (AC-5, AC-7) ----

    [Theory]
    [InlineData("sample.docx", nameof(OfficeKind.Docx))]
    [InlineData("sample.xlsx", nameof(OfficeKind.Xlsx))]
    [InlineData("sample.pptx", nameof(OfficeKind.Pptx))]
    public void SniffZip_CommittedSamples_DocxXlsxPptx(string file, string kind)
    {
        // Act
        var sniff = OfficeSniffer.SniffZip(Sample(file));

        // Assert
        sniff.Should().Be(new OfficeSniff(Enum.Parse<OfficeKind>(kind), null));
    }

    [Fact]
    public void SniffZip_BuiltDocxXlsxPptx_Kinds()
    {
        // Act & Assert
        OfficeSniffer.SniffZip(OfficeBytes.Docx()).Kind.Should().Be(OfficeKind.Docx);
        OfficeSniffer.SniffZip(OfficeBytes.Xlsx()).Kind.Should().Be(OfficeKind.Xlsx);
        OfficeSniffer.SniffZip(OfficeBytes.Pptx()).Kind.Should().Be(OfficeKind.Pptx);
    }

    [Fact]
    public void SniffZip_StrictOoxmlNamespaceSameMainType_Docx()
    {
        // Arrange: ISO strict packages use the same content type; the main part carries the strict namespace.
        var strict = Encoding.UTF8.GetBytes("<w:document xmlns:w=\"http://purl.oclc.org/ooxml/wordprocessingml/main\"><w:body/></w:document>");
        var bytes = OfficeBytes.Package(
            OfficeBytes.ContentTypes([("/word/document.xml", OfficeBytes.DocxMain)]),
            OfficeBytes.Stored("_rels/.rels", OfficeBytes.Rels("word/document.xml")),
            OfficeBytes.Stored("word/document.xml", strict));

        // Act
        var sniff = OfficeSniffer.SniffZip(bytes);

        // Assert
        sniff.Kind.Should().Be(OfficeKind.Docx);
    }

    [Fact]
    public void SniffZip_DocxWithHyperlinksImagesCommentsCharts_Docx()
    {
        // Arrange: an embedded xlsx chart package with the plain sheet content type is ordinary content.
        var extra = "<Default Extension=\"png\" ContentType=\"image/png\"/>"
            + "<Override PartName=\"/word/comments.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.comments+xml\"/>"
            + "<Override PartName=\"/word/charts/chart1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.drawingml.chart+xml\"/>"
            + "<Override PartName=\"/word/embeddings/Microsoft_Excel_Worksheet.xlsx\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet\"/>";
        var bytes = OfficeBytes.DocxWith(
            extra,
            OfficeBytes.Stored("word/_rels/document.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId9\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink\" Target=\"https://example.invalid/\" TargetMode=\"External\"/></Relationships>"),
            OfficeBytes.Stored("word/media/image1.png", new byte[16]),
            OfficeBytes.Stored("word/comments.xml", "<c/>"),
            OfficeBytes.Stored("word/charts/chart1.xml", "<c/>"),
            OfficeBytes.Stored("word/embeddings/Microsoft_Excel_Worksheet.xlsx", OfficeBytes.Xlsx()));

        // Act
        var sniff = OfficeSniffer.SniffZip(bytes);

        // Assert
        sniff.Should().Be(new OfficeSniff(OfficeKind.Docx, null));
    }

    [Fact]
    public void SniffZip_DocxWithOleObjectAndRemoteAttachedTemplate_Docx()
    {
        // Arrange: owner decision OQ-2 (b) — OLE objects and external relationships are not refused; .rels parts are never read.
        var extra = "<Default Extension=\"bin\" ContentType=\"application/vnd.openxmlformats-officedocument.oleObject\"/>";
        var bytes = OfficeBytes.DocxWith(
            extra,
            OfficeBytes.Stored("word/embeddings/oleObject1.bin", OfficeBytes.Cfb(false)),
            OfficeBytes.Stored("word/_rels/settings.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/attachedTemplate\" Target=\"https://evil.invalid/t.dotm\" TargetMode=\"External\"/></Relationships>"));

        // Act
        var sniff = OfficeSniffer.SniffZip(bytes);

        // Assert
        sniff.Should().Be(new OfficeSniff(OfficeKind.Docx, null));
    }

    [Fact]
    public void SniffZip_DocxWithSecretShapedTextInDocumentXml_Docx()
    {
        // Arrange: AC-11's premise — Office content is not scanned, so the sniffer accepts regardless of the text.
        var document = Encoding.UTF8.GetBytes("<w:document xmlns:w=\"x\"><w:body><w:p><w:r><w:t>AKIAIOSFODNN7EXAMPLE</w:t></w:r></w:p></w:body></w:document>");
        var bytes = OfficeBytes.Package(
            OfficeBytes.ContentTypes([("/word/document.xml", OfficeBytes.DocxMain)]),
            OfficeBytes.Stored("_rels/.rels", OfficeBytes.Rels("word/document.xml")),
            OfficeBytes.Stored("word/document.xml", document));

        // Act
        var sniff = OfficeSniffer.SniffZip(bytes);

        // Assert
        sniff.Kind.Should().Be(OfficeKind.Docx);
    }

    [Fact]
    public void SniffZip_DocxWithDocPropsPlaceholders_Docx()
    {
        // Arrange
        var extra = "<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/>"
            + "<Override PartName=\"/docProps/app.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.extended-properties+xml\"/>";
        var bytes = OfficeBytes.DocxWith(
            extra,
            OfficeBytes.Stored("docProps/core.xml", "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:creator>placeholder</dc:creator></cp:coreProperties>"),
            OfficeBytes.Stored("docProps/app.xml", "<Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\"><Application>placeholder</Application></Properties>"));

        // Act
        var sniff = OfficeSniffer.SniffZip(bytes);

        // Assert
        sniff.Kind.Should().Be(OfficeKind.Docx);
    }

    // ---- refused by content type or name (AC-6, AC-7) ----

    [Theory]
    [MemberData(nameof(MacroEnabledMainTypes))]
    public void SniffZip_MacroEnabledContentType_MacroEnabled(string mainType)
    {
        // Act
        var sniff = OfficeSniffer.SniffZip(OfficeBytes.WithMainType(mainType));

        // Assert
        sniff.Should().Be(new OfficeSniff(null, OfficeRefusalDetail.MacroEnabled));
    }

    [Fact]
    public void SniffZip_DocxEmbeddingMacroEnabledPackage_MacroEnabled()
    {
        // Arrange: the main type is plain docx; an embedded xlsm package's content type carries macroEnabled.
        var extra = "<Override PartName=\"/word/embeddings/Microsoft_Excel_Macro-Enabled_Worksheet.xlsm\" ContentType=\"application/vnd.ms-excel.sheet.macroEnabled.12\"/>";
        var bytes = OfficeBytes.DocxWith(extra, OfficeBytes.Stored("word/embeddings/Microsoft_Excel_Macro-Enabled_Worksheet.xlsm", new byte[8]));

        // Act
        var sniff = OfficeSniffer.SniffZip(bytes);

        // Assert
        sniff.Detail.Should().Be(OfficeRefusalDetail.MacroEnabled);
    }

    [Fact]
    public void SniffZip_DocmRenamedDocx_MacroEnabled()
    {
        // Act: the sniffer has no name parameter — the committed docm's bytes under any name.
        var sniff = OfficeSniffer.SniffZip(Sample("sample.docm"));

        // Assert
        sniff.Should().Be(new OfficeSniff(null, OfficeRefusalDetail.MacroEnabled));
    }

    [Fact]
    public void SniffZip_MacroEnabledCaseVariant_MacroEnabled()
    {
        // Act
        var sniff = OfficeSniffer.SniffZip(OfficeBytes.WithMainType("application/vnd.ms-word.document.MACROENABLED.main+xml"));

        // Assert
        sniff.Detail.Should().Be(OfficeRefusalDetail.MacroEnabled);
    }

    [Theory]
    [MemberData(nameof(ActiveContentTypes))]
    public void SniffZip_ActiveContentType_ActiveContent(string contentType)
    {
        // Arrange: declared once as an Override, once as a Default; the entry itself under a harmless name.
        var asOverride = OfficeBytes.DocxWith($"<Override PartName=\"/word/part.dat\" ContentType=\"{contentType}\"/>", OfficeBytes.Stored("word/part.dat", new byte[4]));
        var asDefault = OfficeBytes.DocxWith($"<Default Extension=\"dat\" ContentType=\"{contentType}\"/>", OfficeBytes.Stored("word/part.dat", new byte[4]));

        // Act & Assert
        OfficeSniffer.SniffZip(asOverride).Should().Be(new OfficeSniff(null, OfficeRefusalDetail.ActiveContent));
        OfficeSniffer.SniffZip(asDefault).Should().Be(new OfficeSniff(null, OfficeRefusalDetail.ActiveContent));
    }

    [Theory]
    [InlineData("word/vbaProject.bin")]
    [InlineData("WORD/VBAPROJECT.BIN")]
    [InlineData("word/vbaData.xml")]
    [InlineData("xl/activeX/activeX1.xml")]
    [InlineData("xl/macrosheets/sheet1.xml")]
    public void SniffZip_ActiveContentEntryName_ActiveContent(string entryName)
    {
        // Arrange: a harmless content type (the default "bin"/"xml" types) — the name alone refuses.
        var bytes = OfficeBytes.DocxWith("<Default Extension=\"bin\" ContentType=\"application/octet-stream\"/>", OfficeBytes.Stored(entryName, new byte[4]));

        // Act
        var sniff = OfficeSniffer.SniffZip(bytes);

        // Assert
        sniff.Should().Be(new OfficeSniff(null, OfficeRefusalDetail.ActiveContent));
    }

    [Fact]
    public void SniffZip_MacroEnabledWinsOverActiveContent()
    {
        // Arrange: both present → macro_enabled (check order).
        var bytes = OfficeBytes.WithMainType(
            "application/vnd.ms-word.document.macroEnabled.main+xml",
            "/word/document.xml",
            OfficeBytes.Stored("word/vbaProject.bin", new byte[4]));

        // Act
        var sniff = OfficeSniffer.SniffZip(bytes);

        // Assert
        sniff.Detail.Should().Be(OfficeRefusalDetail.MacroEnabled);
    }

    // ---- other Office-shaped inputs (AC-8) ----

    [Theory]
    [MemberData(nameof(VariantMainTypes))]
    public void SniffZip_VariantMainType_OfficeVariant(string mainType)
    {
        // Act
        var sniff = OfficeSniffer.SniffZip(OfficeBytes.WithMainType(mainType));

        // Assert
        sniff.Should().Be(new OfficeSniff(null, OfficeRefusalDetail.OfficeVariant));
    }

    [Fact]
    public void SniffCompoundFile_PlainHeader_LegacyBinary()
    {
        // Act & Assert
        OfficeSniffer.IsCompoundFile(OfficeBytes.Cfb(false)).Should().BeTrue();
        OfficeSniffer.SniffCompoundFile(OfficeBytes.Cfb(false)).Should().Be(new OfficeSniff(null, OfficeRefusalDetail.LegacyBinary));
        OfficeSniffer.SniffCompoundFile(Sample("sample.doc")).Should().Be(new OfficeSniff(null, OfficeRefusalDetail.LegacyBinary));
    }

    [Fact]
    public void SniffCompoundFile_EncryptedPackageEntry_Encrypted()
    {
        // Act
        var sniff = OfficeSniffer.SniffCompoundFile(OfficeBytes.Cfb(true));

        // Assert
        sniff.Should().Be(new OfficeSniff(null, OfficeRefusalDetail.Encrypted));
    }

    [Fact]
    public void SniffCompoundFile_TruncatedHeader_LegacyBinary()
    {
        // Act & Assert: a header without its directory sector, a short header, an invalid sector shift.
        OfficeSniffer.SniffCompoundFile(OfficeBytes.CfbHeaderOnly()).Detail.Should().Be(OfficeRefusalDetail.LegacyBinary);
        OfficeSniffer.SniffCompoundFile(OfficeBytes.CfbHeaderOnly().AsSpan(0, 64)).Detail.Should().Be(OfficeRefusalDetail.LegacyBinary);
        OfficeSniffer.SniffCompoundFile(OfficeBytes.CfbHeaderOnly(sectorShift: 3)).Detail.Should().Be(OfficeRefusalDetail.LegacyBinary);
    }

    [Theory]
    [InlineData("application/vnd.oasis.opendocument.text")]
    [InlineData("application/vnd.oasis.opendocument.spreadsheet")]
    [InlineData("application/vnd.oasis.opendocument.presentation")]
    public void SniffZip_OpenDocumentMimetype_OpenDocument(string mimetype)
    {
        // Act & Assert
        OfficeSniffer.SniffZip(OfficeBytes.Odf(mimetype)).Should().Be(new OfficeSniff(null, OfficeRefusalDetail.OpenDocument));
        OfficeSniffer.SniffZip(Sample("sample.odt")).Should().Be(new OfficeSniff(null, OfficeRefusalDetail.OpenDocument));
    }

    [Fact]
    public void SniffZip_NoContentTypes_NotOffice()
    {
        // Act & Assert
        OfficeSniffer.SniffZip(Sample("plain.zip")).Should().Be(new OfficeSniff(null, OfficeRefusalDetail.NotOffice));
        OfficeSniffer.SniffZip(OfficeBytes.Zip(OfficeBytes.Stored("readme.txt", "hi"))).Detail.Should().Be(OfficeRefusalDetail.NotOffice);
    }

    [Fact]
    public void SniffZip_TwoAcceptedMainTypes_NotOffice()
    {
        // Arrange
        var bytes = OfficeBytes.Package(
            OfficeBytes.ContentTypes([("/word/document.xml", OfficeBytes.DocxMain), ("/xl/workbook.xml", OfficeBytes.XlsxMain)]),
            OfficeBytes.Stored("word/document.xml", "<d/>"),
            OfficeBytes.Stored("xl/workbook.xml", "<w/>"));

        // Act
        var sniff = OfficeSniffer.SniffZip(bytes);

        // Assert
        sniff.Should().Be(new OfficeSniff(null, OfficeRefusalDetail.NotOffice));
    }

    [Fact]
    public void SniffZip_NoAcceptedMainType_NotOffice()
    {
        // Arrange: a content-types file naming only a Visio main part.
        var bytes = OfficeBytes.Package(
            OfficeBytes.ContentTypes([("/visio/document.xml", "application/vnd.ms-visio.drawing.main+xml")]),
            OfficeBytes.Stored("visio/document.xml", "<v/>"));

        // Act
        var sniff = OfficeSniffer.SniffZip(bytes);

        // Assert
        sniff.Should().Be(new OfficeSniff(null, OfficeRefusalDetail.NotOffice));
    }

    [Fact]
    public void SniffZip_MainPartMissing_NotOffice()
    {
        // Arrange: declared but absent.
        var bytes = OfficeBytes.Package(OfficeBytes.ContentTypes([("/word/document.xml", OfficeBytes.DocxMain)]), OfficeBytes.Stored("_rels/.rels", OfficeBytes.Rels("word/document.xml")));

        // Act
        var sniff = OfficeSniffer.SniffZip(bytes);

        // Assert
        sniff.Should().Be(new OfficeSniff(null, OfficeRefusalDetail.NotOffice));
    }

    [Fact]
    public void SniffZip_OpenDocumentMimetypeNotFirstEntry_NotOffice()
    {
        // Arrange: plan assumption A3 — only a first `mimetype` entry is consulted.
        var bytes = OfficeBytes.Zip(OfficeBytes.Stored("content.xml", "<c/>"), OfficeBytes.Stored("mimetype", "application/vnd.oasis.opendocument.text"));

        // Act
        var sniff = OfficeSniffer.SniffZip(bytes);

        // Assert
        sniff.Should().Be(new OfficeSniff(null, OfficeRefusalDetail.NotOffice));
    }

    // ---- malformed and hostile (AC-9) ----

    [Fact]
    public void SniffZip_NotAZip_ZipMalformed()
    {
        // Arrange
        var bytes = new byte[256];
        new Random(39).NextBytes(bytes);
        bytes[0] = 0x50;
        bytes[1] = 0x4B;
        bytes[2] = 0x03;
        bytes[3] = 0x04;

        // Act
        var sniff = OfficeSniffer.SniffZip(bytes);

        // Assert
        OfficeSniffer.IsZip(bytes).Should().BeTrue();
        sniff.Should().Be(new OfficeSniff(null, OfficeRefusalDetail.ZipMalformed));
    }

    [Fact]
    public void SniffZip_EncryptedEntry_ZipMalformed()
    {
        // Arrange
        var bytes = OfficeBytes.Package(
            OfficeBytes.ContentTypes([("/word/document.xml", OfficeBytes.DocxMain)]),
            OfficeBytes.Stored("word/document.xml", "<d/>") with { Encrypted = true });

        // Act
        var sniff = OfficeSniffer.SniffZip(bytes);

        // Assert
        sniff.Should().Be(new OfficeSniff(null, OfficeRefusalDetail.ZipMalformed));
    }

    [Fact]
    public void SniffZip_DuplicateNamesIgnoringCase_ZipMalformed()
    {
        // Arrange
        var bytes = OfficeBytes.Package(
            OfficeBytes.ContentTypes([("/word/document.xml", OfficeBytes.DocxMain)]),
            OfficeBytes.Stored("word/document.xml", "<d/>"),
            OfficeBytes.Stored("Word/Document.xml", "<e/>"));

        // Act
        var sniff = OfficeSniffer.SniffZip(bytes);

        // Assert
        sniff.Should().Be(new OfficeSniff(null, OfficeRefusalDetail.ZipMalformed));
    }

    [Theory]
    [MemberData(nameof(UnsafeNames))]
    public void SniffZip_UnsafeEntryName_ZipMalformed(string name)
    {
        // Arrange
        var bytes = OfficeBytes.Docx(OfficeBytes.Stored(name, "<x/>"));

        // Act
        var sniff = OfficeSniffer.SniffZip(bytes);

        // Assert
        sniff.Should().Be(new OfficeSniff(null, OfficeRefusalDetail.ZipMalformed));
    }

    [Fact]
    public void SniffZip_ContentTypesNotWellFormed_ZipMalformed()
    {
        // Act
        var sniff = OfficeSniffer.SniffZip(OfficeBytes.WithContentTypes("<Types><Override PartName=\"/word/document.xml\" ContentType=\"x\">"));

        // Assert
        sniff.Should().Be(new OfficeSniff(null, OfficeRefusalDetail.ZipMalformed));
    }

    [Fact]
    public void SniffZip_ContentTypesWithDtd_ZipMalformed()
    {
        // Arrange
        var xml = "<?xml version=\"1.0\"?><!DOCTYPE Types [<!ENTITY x \"y\">]><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
            + $"<Override PartName=\"/word/document.xml\" ContentType=\"{OfficeBytes.DocxMain}\"/></Types>";

        // Act
        var sniff = OfficeSniffer.SniffZip(OfficeBytes.WithContentTypes(xml));

        // Assert
        sniff.Should().Be(new OfficeSniff(null, OfficeRefusalDetail.ZipMalformed));
    }

    [Fact]
    public void SniffZip_TenThousandAndOneEntries_ZipBounds()
    {
        // Arrange
        var entries = Enumerable.Range(0, OfficeSniffer.MaxEntries + 1).Select(i => OfficeBytes.Stored($"p/{i}", ""));

        // Act
        var sniff = OfficeSniffer.SniffZip(OfficeBytes.Zip(entries));

        // Assert
        sniff.Should().Be(new OfficeSniff(null, OfficeRefusalDetail.ZipBounds));
    }

    [Fact]
    public void SniffZip_TenThousandEntries_NotBounds()
    {
        // Arrange: the boundary — exactly MaxEntries entries, no content types.
        var entries = Enumerable.Range(0, OfficeSniffer.MaxEntries).Select(i => OfficeBytes.Stored($"p/{i}", ""));

        // Act
        var sniff = OfficeSniffer.SniffZip(OfficeBytes.Zip(entries));

        // Assert
        sniff.Detail.Should().Be(OfficeRefusalDetail.NotOffice);
    }

    [Fact]
    public void SniffZip_DeclaredSizesPastOneGiB_ZipBounds()
    {
        // Arrange: two entries each declaring 0x7FFFFFFF uncompressed bytes in a file of a few hundred bytes.
        var bytes = OfficeBytes.Package(
            OfficeBytes.ContentTypes([("/word/document.xml", OfficeBytes.DocxMain)]),
            OfficeBytes.Stored("word/document.xml", "<d/>") with { DeclaredSize = 0x7FFFFFFF },
            OfficeBytes.Stored("word/styles.xml", "<s/>") with { DeclaredSize = 0x7FFFFFFF });

        // Act
        var sniff = OfficeSniffer.SniffZip(bytes);

        // Assert
        bytes.Length.Should().BeLessThan(2048);
        sniff.Should().Be(new OfficeSniff(null, OfficeRefusalDetail.ZipBounds));
    }

    [Fact]
    public void SniffZip_ContentTypesInflatesPastFourMiB_ZipBoundsUnderOneSecond()
    {
        // Arrange: a 5 MiB [Content_Types].xml of spaces deflated to a few KB.
        var bomb = OfficeBytes.Zip(
            OfficeBytes.Deflated("[Content_Types].xml", OfficeBytes.Spaces(5 * 1024 * 1024)),
            OfficeBytes.Stored("word/document.xml", "<d/>"));
        var watch = Stopwatch.StartNew();

        // Act
        var sniff = OfficeSniffer.SniffZip(bomb);

        // Assert
        watch.Stop();
        bomb.Length.Should().BeLessThan(64 * 1024);
        sniff.Should().Be(new OfficeSniff(null, OfficeRefusalDetail.ZipBounds));
        watch.ElapsedMilliseconds.Should().BeLessThan(1000);
    }

    [Fact]
    public void SniffZip_MimetypeInflatesPastFourMiB_ZipBounds()
    {
        // Arrange
        var bytes = OfficeBytes.Zip(OfficeBytes.Deflated("mimetype", OfficeBytes.Spaces(5 * 1024 * 1024)), OfficeBytes.Stored("content.xml", "<c/>"));

        // Act
        var sniff = OfficeSniffer.SniffZip(bytes);

        // Assert
        sniff.Should().Be(new OfficeSniff(null, OfficeRefusalDetail.ZipBounds));
    }

    [Fact]
    public void IsZip_JunkBeforeSignature_False()
    {
        // Arrange: a polyglot with bytes before the local header is not a ZIP for the archive.
        var bytes = (byte[])[0x00, .. OfficeBytes.Docx()];

        // Act & Assert
        OfficeSniffer.IsZip(bytes).Should().BeFalse();
        OfficeSniffer.IsZip(OfficeBytes.Docx()).Should().BeTrue();
        OfficeSniffer.IsZip([0x50, 0x4B]).Should().BeFalse();
    }

    [Fact]
    public void SniffZip_OnlyTwoEntriesEverOpened()
    {
        // Arrange: every entry other than [Content_Types].xml would inflate past 4 MiB if opened — the sniffer still accepts.
        var huge = OfficeBytes.Spaces(5 * 1024 * 1024);
        var bytes = OfficeBytes.Package(
            OfficeBytes.ContentTypes([("/word/document.xml", OfficeBytes.DocxMain)]),
            OfficeBytes.Deflated("_rels/.rels", huge),
            OfficeBytes.Deflated("word/document.xml", huge),
            OfficeBytes.Deflated("word/styles.xml", huge));

        // Act
        var sniff = OfficeSniffer.SniffZip(bytes);

        // Assert
        sniff.Should().Be(new OfficeSniff(OfficeKind.Docx, null));
    }

    // ---- wire ----

    [Fact]
    public void OfficeRefusalDetailWire_Rows()
    {
        // Arrange
        var expected = new Dictionary<OfficeRefusalDetail, string>
        {
            [OfficeRefusalDetail.MacroEnabled] = "macro_enabled",
            [OfficeRefusalDetail.ActiveContent] = "active_content",
            [OfficeRefusalDetail.LegacyBinary] = "legacy_binary",
            [OfficeRefusalDetail.Encrypted] = "encrypted",
            [OfficeRefusalDetail.OpenDocument] = "opendocument",
            [OfficeRefusalDetail.OfficeVariant] = "office_variant",
            [OfficeRefusalDetail.NotOffice] = "not_office",
            [OfficeRefusalDetail.ZipMalformed] = "zip_malformed",
            [OfficeRefusalDetail.ZipBounds] = "zip_bounds",
        };

        // Act
        var actual = Enum.GetValues<OfficeRefusalDetail>().ToDictionary(d => d, OfficeRefusalDetailWire.ToWire);

        // Assert
        actual.Should().Equal(expected);
    }

    [Fact]
    public void OfficeRefusalDetailWire_EveryMember_HasARow()
    {
        // Assert: the theory above lists nine details.
        Enum.GetValues<OfficeRefusalDetail>().Should().HaveCount(9);
        Enum.GetValues<OfficeKind>().Should().HaveCount(3);
    }

    [Theory]
    [MemberData(nameof(EverySample))]
    public void OfficeSniff_NeverBothKindAndDetail(string name, byte[] bytes)
    {
        // Act
        var sniff = Sniff(bytes);

        // Assert
        name.Should().NotBeEmpty();
        (sniff.Kind is null ^ sniff.Detail is null).Should().BeTrue();
    }
}
