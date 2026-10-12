using Zyggy.Core.Memory;

namespace Zyggy.Core.Tests.Memory;

public sealed class ArchiveMediaTypeWireTests
{
    [Theory]
    [InlineData(ArchiveMediaType.TextPlain, "text/plain")]
    [InlineData(ArchiveMediaType.TextMarkdown, "text/markdown")]
    [InlineData(ArchiveMediaType.Pdf, "application/pdf")]
    [InlineData(ArchiveMediaType.Png, "image/png")]
    [InlineData(ArchiveMediaType.Jpeg, "image/jpeg")]
    [InlineData(ArchiveMediaType.Gif, "image/gif")]
    [InlineData(ArchiveMediaType.Docx, "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData(ArchiveMediaType.Xlsx, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    [InlineData(ArchiveMediaType.Pptx, "application/vnd.openxmlformats-officedocument.presentationml.presentation")]
    public void ToWire_Member_ReturnsMediaType(ArchiveMediaType type, string wire)
    {
        // Act
        var actual = ArchiveMediaTypeWire.ToWire(type);

        // Assert
        actual.Should().Be(wire);
        ArchiveMediaTypeWire.TryFromWire(wire, out var back).Should().BeTrue();
        back.Should().Be(type);
    }

    [Theory]
    [InlineData(ArchiveMediaType.TextPlain, "txt")]
    [InlineData(ArchiveMediaType.TextMarkdown, "txt")]
    [InlineData(ArchiveMediaType.Pdf, "pdf")]
    [InlineData(ArchiveMediaType.Png, "png")]
    [InlineData(ArchiveMediaType.Jpeg, "jpg")]
    [InlineData(ArchiveMediaType.Gif, "gif")]
    [InlineData(ArchiveMediaType.Docx, "docx")]
    [InlineData(ArchiveMediaType.Xlsx, "xlsx")]
    [InlineData(ArchiveMediaType.Pptx, "pptx")]
    public void Extension_Member_ReturnsCanonicalExtension(ArchiveMediaType type, string extension)
    {
        // Act
        var actual = ArchiveMediaTypeWire.Extension(type);

        // Assert
        actual.Should().Be(extension);
    }

    [Fact]
    public void TryFromWire_RoundTripsEveryMember()
    {
        // Act
        var back = ArchiveMediaTypeWire.All.Select(member => ArchiveMediaTypeWire.TryFromWire(ArchiveMediaTypeWire.ToWire(member), out var type) ? type : (ArchiveMediaType?)null);

        // Assert
        back.Should().Equal(ArchiveMediaTypeWire.All.Cast<ArchiveMediaType?>());
        ArchiveMediaTypeWire.All.Should().HaveCount(9);
    }

    [Theory]
    [InlineData("txt", ArchiveMediaType.TextPlain)]
    [InlineData("pdf", ArchiveMediaType.Pdf)]
    [InlineData("png", ArchiveMediaType.Png)]
    [InlineData("jpg", ArchiveMediaType.Jpeg)]
    [InlineData("gif", ArchiveMediaType.Gif)]
    [InlineData("docx", ArchiveMediaType.Docx)]
    [InlineData("xlsx", ArchiveMediaType.Xlsx)]
    [InlineData("pptx", ArchiveMediaType.Pptx)]
    [InlineData("md", null)]
    [InlineData("jpeg", null)]
    [InlineData("PNG", null)]
    [InlineData("DOCX", null)]
    [InlineData("docm", null)]
    [InlineData("xlsm", null)]
    [InlineData("pptm", null)]
    [InlineData("doc", null)]
    [InlineData("xls", null)]
    [InlineData("ppt", null)]
    [InlineData("odt", null)]
    [InlineData("ods", null)]
    [InlineData("odp", null)]
    [InlineData("zip", null)]
    [InlineData("dotx", null)]
    [InlineData("ppsx", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TryFromExtension_Rows(string? extension, ArchiveMediaType? expected)
    {
        // Act
        var recognised = ArchiveMediaTypeWire.TryFromExtension(extension, out var type);

        // Assert
        recognised.Should().Be(expected is not null);
        if (expected is { } member)
        {
            type.Should().Be(member);
        }
    }

    [Fact]
    public void IsItemExtension_AgreesWithTryFromExtension()
    {
        // Act
        var extensions = ArchiveMediaTypeWire.All.Select(ArchiveMediaTypeWire.Extension).Distinct().ToList();

        // Assert
        extensions.Should().OnlyContain(extension => ArchiveMediaTypeWire.IsItemExtension(extension));
        extensions.All(extension => ArchiveMediaTypeWire.TryFromExtension(extension, out var type) && ArchiveMediaTypeWire.Extension(type) == extension).Should().BeTrue();
        extensions.Should().HaveCount(8);
    }

    [Theory]
    [InlineData(ArchiveMediaType.TextPlain, "text/plain")]
    [InlineData(ArchiveMediaType.TextMarkdown, "text/markdown")]
    [InlineData(ArchiveMediaType.Pdf, "application/pdf")]
    [InlineData(ArchiveMediaType.Png, "image/png")]
    [InlineData(ArchiveMediaType.Jpeg, "image/jpeg")]
    [InlineData(ArchiveMediaType.Gif, "image/gif")]
    [InlineData(ArchiveMediaType.Docx, "Word document")]
    [InlineData(ArchiveMediaType.Xlsx, "Excel workbook")]
    [InlineData(ArchiveMediaType.Pptx, "PowerPoint deck")]
    public void Label_Rows(ArchiveMediaType type, string label)
    {
        // Act
        var actual = ArchiveMediaTypeWire.Label(type);

        // Assert
        actual.Should().Be(label);
    }

    [Fact]
    public void Label_NeverLongerThanApplicationPdf()
    {
        // Assert: the premise of ArchiveIndexLine.Longest, which composes application/pdf.
        ArchiveMediaTypeWire.All.Select(ArchiveMediaTypeWire.Label).Should().OnlyContain(label => label.Length <= "application/pdf".Length);
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("")]
    [InlineData(null)]
    public void TryFromWire_Unknown_ReturnsFalse(string? wire)
    {
        // Act
        var recognised = ArchiveMediaTypeWire.TryFromWire(wire, out _);

        // Assert
        recognised.Should().BeFalse();
    }

    [Theory]
    [InlineData("txt", true)]
    [InlineData("pdf", true)]
    [InlineData("png", true)]
    [InlineData("jpg", true)]
    [InlineData("gif", true)]
    [InlineData("md", false)]
    [InlineData("jpeg", false)]
    [InlineData("PNG", false)]
    [InlineData("docx", true)]
    [InlineData("xlsx", true)]
    [InlineData("pptx", true)]
    [InlineData("docm", false)]
    [InlineData("xlsm", false)]
    [InlineData("pptm", false)]
    [InlineData("doc", false)]
    [InlineData("odt", false)]
    [InlineData("zip", false)]
    [InlineData("DOCX", false)]
    [InlineData("", false)]
    public void IsItemExtension_ClosedList(string extension, bool expected)
    {
        // Act
        var actual = ArchiveMediaTypeWire.IsItemExtension(extension);

        // Assert
        actual.Should().Be(expected);
    }

    [Fact]
    public void EveryMember_HasATestRow()
    {
        // Assert: the two theories above list every member.
        ArchiveMediaTypeWire.All.Should().BeEquivalentTo(Enum.GetValues<ArchiveMediaType>());
        Enum.GetValues<ArchiveMediaType>().Should().HaveCount(9);
    }
}
