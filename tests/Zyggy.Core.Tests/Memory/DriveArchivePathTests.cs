using Zyggy.Core.Memory;

namespace Zyggy.Core.Tests.Memory;

/// <summary>Spec 38: the one builder of OneDrive paths, <c>&lt;root&gt;/&lt;project&gt;/&lt;slug&gt;.&lt;ext&gt;</c>.</summary>
public sealed class DriveArchivePathTests
{
    [Fact]
    public void Item_DefaultRoot_ZyggyArchiveProjectSlugExt()
    {
        // Act
        var path = new DriveArchivePath("Zyggy/archive").Item(Slug.Parse("zyggy"), Slug.Parse("quote-2026"), "txt");

        // Assert
        path.Should().Be("Zyggy/archive/zyggy/quote-2026.txt");
    }

    [Fact]
    public void Item_ThreeSegmentRoot()
    {
        // Act
        var drive = new DriveArchivePath("Documents/Zyggy/archive");

        // Assert
        drive.Item(Slug.Parse("reading"), Slug.Parse("cover"), "png").Should().Be("Documents/Zyggy/archive/reading/cover.png");
        drive.Segments.Should().Equal("Documents", "Zyggy", "archive");
    }

    [Fact]
    public void ProjectFolder_Slug()
    {
        // Act / Assert
        new DriveArchivePath("Zyggy/archive").ProjectFolder(Slug.Parse("zyggy")).Should().Be("Zyggy/archive/zyggy");
        DriveArchivePath.FileName(Slug.Parse("quote-2026"), "pdf").Should().Be("quote-2026.pdf");
    }
}
