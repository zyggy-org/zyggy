using System.Text;

using Zyggy.Core.Media;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Core.Tests.LinkedIn;

namespace Zyggy.Core.Tests.Media;

public sealed class MediaSnifferTests
{
    public static TheoryData<byte[], bool> Utf8Rows => new()
    {
        { "plain ascii text"u8.ToArray(), true },
        { [0xEF, 0xBB, 0xBF, 0x62, 0x6F, 0x6D], true },
        { Encoding.UTF8.GetBytes("café"), true },
        { "a\tb\nc\r\n"u8.ToArray(), true },
        { [0x61, 0x00, 0x62], false },
        { [0x61, 0x07, 0x62], false },
        { [0xC3, 0x28], false },
        { [], false },
    };

    [Fact]
    public void ImageType_PngGifJpeg_Recognised()
    {
        // Act & Assert
        MediaSniffer.ImageType(ImageBytes.Png(2, 2)).Should().Be("image/png");
        MediaSniffer.ImageType(ImageBytes.Gif(2, 2)).Should().Be("image/gif");
        MediaSniffer.ImageType(ImageBytes.Jpeg(2, 2)).Should().Be("image/jpeg");
        MediaSniffer.ImageType("%PDF-1.4\n"u8).Should().BeNull();
    }

    [Fact]
    public void IsPdf_Header_True()
    {
        // Act
        var actual = MediaSniffer.IsPdf("%PDF-1.4\n1 0 obj<<>>endobj\n"u8);

        // Assert
        actual.Should().BeTrue();
    }

    [Fact]
    public void IsPdf_TextStartingWithPdfWord_False()
    {
        // Act
        var actual = MediaSniffer.IsPdf("PDF-1.4 notes"u8);

        // Assert
        actual.Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(Utf8Rows))]
    public void IsUtf8Text_Rows(byte[] bytes, bool expected)
    {
        // Act
        var actual = MediaSniffer.IsUtf8Text(bytes);

        // Assert
        actual.Should().Be(expected);
    }

    [Fact]
    public void Sniff_PngBytesNamedTxt_ImagePng()
    {
        // Act
        var actual = MediaSniffer.Sniff(ImageBytes.Png(2, 2), markdownName: false).MediaType;

        // Assert
        actual.Should().Be("image/png");
    }

    [Fact]
    public void Sniff_TextWithMarkdownName_TextMarkdown()
    {
        // Act
        var actual = MediaSniffer.Sniff("# Title\n"u8.ToArray(), markdownName: true).MediaType;

        // Assert
        actual.Should().Be("text/markdown");
    }

    [Fact]
    public void Sniff_TextWithTxtName_TextPlain()
    {
        // Act
        var actual = MediaSniffer.Sniff("Quote for the roof.\n"u8.ToArray(), markdownName: false).MediaType;

        // Assert
        actual.Should().Be("text/plain");
    }

    [Fact]
    public void Sniff_RandomBinary_Null()
    {
        // Act
        var actual = MediaSniffer.Sniff([0x00, 0x01, 0x02, 0xFE, 0xFF], markdownName: false).MediaType;

        // Assert
        actual.Should().BeNull();
    }

    [Fact]
    public void Sniff_CommittedDocx_OfficeWireString()
    {
        // Act
        var sniff = MediaSniffer.Sniff(File.ReadAllBytes(Path.Combine(Golden.Directory, "office", "sample.docx")), markdownName: false);

        // Assert
        sniff.Should().Be(new MediaSniff("application/vnd.openxmlformats-officedocument.wordprocessingml.document", null));
    }

    [Fact]
    public void Sniff_DocmBytes_DetailMacroEnabledNoMediaType()
    {
        // Act
        var sniff = MediaSniffer.Sniff(File.ReadAllBytes(Path.Combine(Golden.Directory, "office", "sample.docm")), markdownName: false);

        // Assert
        sniff.Should().Be(new MediaSniff(null, OfficeRefusalDetail.MacroEnabled));
    }

    [Fact]
    public void Sniff_CompoundFile_DetailLegacyBinary()
    {
        // Act
        var sniff = MediaSniffer.Sniff(OfficeBytes.Cfb(false), markdownName: false);

        // Assert
        sniff.Should().Be(new MediaSniff(null, OfficeRefusalDetail.LegacyBinary));
    }

    [Fact]
    public void Sniff_PdfBytes_StillPdfBeforeZipCheck()
    {
        // Act
        var sniff = MediaSniffer.Sniff("%PDF-1.4\nPK\u0003\u0004"u8.ToArray(), markdownName: false);

        // Assert
        sniff.Should().Be(new MediaSniff("application/pdf", null));
    }

    [Fact]
    public void Sniff_PngBytes_StillImageBeforeZipCheck()
    {
        // Act
        var sniff = MediaSniffer.Sniff(ImageBytes.Png(2, 2), markdownName: false);

        // Assert
        sniff.Should().Be(new MediaSniff("image/png", null));
    }

    [Fact]
    public void Sniff_Utf8TextStartingWithPK_TextNotZip()
    {
        // Act: `PK` followed by a space is text, not a local file header.
        var sniff = MediaSniffer.Sniff("PK 2026 quote\n"u8.ToArray(), markdownName: false);

        // Assert
        sniff.Should().Be(new MediaSniff("text/plain", null));
    }

    [Fact]
    public void Sniff_ZipJunkPrefix_NullBoth()
    {
        // Arrange: a byte before the ZIP signature — neither a ZIP nor text.
        byte[] bytes = [0x00, .. OfficeBytes.Docx()];

        // Act
        var sniff = MediaSniffer.Sniff(bytes, markdownName: false);

        // Assert
        sniff.Should().Be(default(MediaSniff));
    }
}
