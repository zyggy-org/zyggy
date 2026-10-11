using System.Text;

using Zyggy.Core.Memory;

namespace Zyggy.Core.Tests.Memory;

/// <summary>Spec 38 AC-13: the offload part after 37's keys, byte-equal to the hand-derived golden; all-or-none on read.</summary>
public sealed class ArchiveSidecarOffloadTests
{
    private const string Url = "https://acme-my.example.invalid/personal/alice/Documents/Zyggy/archive/zyggy/quote-2026.txt";

    private static readonly DateOnly Archived = new(2026, 9, 30);
    private static readonly DateOnly Offloaded = new(2026, 10, 1);

    public static TheoryData<string> RequiredKeys() => new() { "offloaded", "drive_item_id", "drive_path", "drive_quickxor" };

    [Fact]
    public void ToMemoryFile_WithOffload_EqualsGolden()
    {
        // Act
        var rendered = MemoryFileWriter.Render(Sidecar(Url).ToMemoryFile());

        // Assert
        new UTF8Encoding(false).GetBytes(rendered).Should().Equal(Golden("offload/sidecar-offloaded.md"));
    }

    [Fact]
    public void ToMemoryFile_OffloadWithoutUrl_EqualsGolden()
    {
        // Act
        var rendered = MemoryFileWriter.Render(Sidecar(null).ToMemoryFile());

        // Assert
        new UTF8Encoding(false).GetBytes(rendered).Should().Equal(Golden("offload/sidecar-offloaded-no-url.md"));
    }

    [Theory]
    [InlineData("offload/sidecar-offloaded.md", Url)]
    [InlineData("offload/sidecar-offloaded-no-url.md", null)]
    public void TryParse_OffloadedGolden_RoundTrips(string golden, string? url)
    {
        // Act
        var parsed = ArchiveSidecar.TryParse(MemoryFileReader.Parse(Encoding.UTF8.GetString(Golden(golden))));

        // Assert
        parsed.Should().Be(Sidecar(url));
    }

    [Theory]
    [MemberData(nameof(RequiredKeys))]
    public void TryParse_PartialOffloadKeys_Null(string key)
    {
        // Arrange
        var text = string.Join('\n', Encoding.UTF8.GetString(Golden("offload/sidecar-offloaded.md")).Split('\n').Where(l => !l.StartsWith(key + ": ", StringComparison.Ordinal)));

        // Act
        var parsed = ArchiveSidecar.TryParse(MemoryFileReader.Parse(text));

        // Assert
        parsed.Should().BeNull();
    }

    [Fact]
    public void TryParse_MalformedOffloadedDate_Null()
    {
        // Arrange
        var text = Encoding.UTF8.GetString(Golden("offload/sidecar-offloaded.md")).Replace("offloaded: 2026-10-01", "offloaded: yesterday", StringComparison.Ordinal);

        // Act / Assert
        ArchiveSidecar.TryParse(MemoryFileReader.Parse(text)).Should().BeNull();
    }

    [Theory]
    [InlineData("sidecar-text")]
    [InlineData("sidecar-png")]
    [InlineData("sidecar-pdf")]
    public void ToMemoryFile_WithoutOffload_ByteIdenticalTo37Goldens(string golden)
    {
        // Arrange
        var bytes = Golden(golden + ".md");
        var parsed = ArchiveSidecar.TryParse(MemoryFileReader.Parse(Encoding.UTF8.GetString(bytes)))!;

        // Act
        var rendered = MemoryFileWriter.Render(parsed.ToMemoryFile());

        // Assert
        parsed.Offload.Should().BeNull();
        new UTF8Encoding(false).GetBytes(rendered).Should().Equal(bytes);
    }

    private static byte[] Golden(string name) => File.ReadAllBytes(Path.Combine(Infrastructure.Golden.Directory, "archive", name));

    private static ArchiveSidecar Sidecar(string? url) =>
        new ArchiveSidecar("Quote 2026", "Roof repair quote", Slug.Parse("zyggy"), ArchiveMediaType.TextPlain, 68,
            "c5f2cab5d3b4d46c44bee6f1ee037d471c419b739908f4a05251745fb4794f88", Archived, "quote.txt", Offloaded)
        {
            Offload = new ArchiveOffload(Offloaded, "01ITEMQUOTE", "Zyggy/archive/zyggy/quote-2026.txt", url, "jAt95bF64I8MSmyO3joPMtROLIU="),
        };
}
