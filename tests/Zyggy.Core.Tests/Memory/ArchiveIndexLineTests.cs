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
