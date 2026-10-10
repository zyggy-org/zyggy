using System.Text;

using Zyggy.Core.Media;
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
        var actual = MediaSniffer.Sniff(ImageBytes.Png(2, 2), markdownName: false);

        // Assert
        actual.Should().Be("image/png");
    }

    [Fact]
    public void Sniff_TextWithMarkdownName_TextMarkdown()
    {
        // Act
        var actual = MediaSniffer.Sniff("# Title\n"u8, markdownName: true);

        // Assert
        actual.Should().Be("text/markdown");
    }

    [Fact]
    public void Sniff_TextWithTxtName_TextPlain()
    {
        // Act
        var actual = MediaSniffer.Sniff("Quote for the roof.\n"u8, markdownName: false);

        // Assert
        actual.Should().Be("text/plain");
    }

    [Fact]
    public void Sniff_RandomBinary_Null()
    {
        // Act
        var actual = MediaSniffer.Sniff([0x00, 0x01, 0x02, 0xFE, 0xFF], markdownName: false);

        // Assert
        actual.Should().BeNull();
    }
}
