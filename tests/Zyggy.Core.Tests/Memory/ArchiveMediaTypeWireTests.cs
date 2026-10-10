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
    public void Extension_Member_ReturnsCanonicalExtension(ArchiveMediaType type, string extension)
    {
        // Act
        var actual = ArchiveMediaTypeWire.Extension(type);

        // Assert
        actual.Should().Be(extension);
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
    [InlineData("docx", false)]
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
        Enum.GetValues<ArchiveMediaType>().Should().HaveCount(6);
    }
}
