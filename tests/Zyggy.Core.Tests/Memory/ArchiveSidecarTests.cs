using System.Text;

using Zyggy.Core.Memory;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Memory;

/// <summary>The sidecar of an archived item (spec 37 AC-13): rendered bytes equal the golden, readable back through <see cref="MemoryFileReader"/>.</summary>
public sealed class ArchiveSidecarTests
{
    private static readonly DateOnly Date = new(2026, 9, 30);

    public static TheoryData<string> Cases() => new() { "sidecar-text", "sidecar-png", "sidecar-pdf", "sidecar-docx", "sidecar-xlsx", "sidecar-pptx" };

    [Theory]
    [MemberData(nameof(Cases))]
    public void ToMemoryFile_Rendered_EqualsGolden(string golden)
    {
        // Act
        var rendered = new UTF8Encoding(false).GetBytes(MemoryFileWriter.Render(Sidecar(golden).ToMemoryFile()));

        // Assert
        rendered.Should().Equal(GoldenBytes(golden));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TryParse_Golden_RoundTrips(string golden)
    {
        // Act
        var parsed = ArchiveSidecar.TryParse(MemoryFileReader.Parse(Encoding.UTF8.GetString(GoldenBytes(golden))));

        // Assert
        parsed.Should().Be(Sidecar(golden));
    }

    [Fact]
    public void TryParse_MissingKey_Null()
    {
        // Arrange
        var text = Encoding.UTF8.GetString(GoldenBytes("sidecar-text")).Replace("sha256: ", "digest: ", StringComparison.Ordinal);

        // Act
        var parsed = ArchiveSidecar.TryParse(MemoryFileReader.Parse(text));

        // Assert
        parsed.Should().BeNull();
    }

    [Fact]
    public void ToMemoryFile_NoAliasesNoBulletLines()
    {
        // Act
        var file = Sidecar("sidecar-text").ToMemoryFile();

        // Assert
        file.Aliases.Should().BeEmpty();
        file.BodyLines.Should().Equal("Roof repair quote");
        file.HasFrontMatter.Should().BeTrue();
        file.UnknownKeys.Keys.Should().Equal("project", "media_type", "size_bytes", "sha256", "archived", "source_name");
    }

    private static byte[] GoldenBytes(string name) => File.ReadAllBytes(Path.Combine(Golden.Directory, "archive", name + ".md"));

    private static ArchiveSidecar Sidecar(string golden) => golden switch
    {
        "sidecar-text" => new ArchiveSidecar("Quote 2026", "Roof repair quote", Slug.Parse("zyggy"), ArchiveMediaType.TextPlain, 68,
            "c5f2cab5d3b4d46c44bee6f1ee037d471c419b739908f4a05251745fb4794f88", Date, "quote.txt", Date),
        "sidecar-png" => new ArchiveSidecar("Roof photo", "Photo of the roof damage", Slug.Parse("zyggy"), ArchiveMediaType.Png, 65,
            "6ebc44920b939cfbce21e4ec5710357c44156b9afad130783806a4983a53b8af", Date, "roof.png", Date),
        "sidecar-pdf" => new ArchiveSidecar("Roof invoice", "Invoice for the roof repair", Slug.Parse("zyggy"), ArchiveMediaType.Pdf, 45,
            "5a838678058f6de375e8635b5f2fea47a4e5f07cb1a882a44b10f39abc6f34ff", Date, "invoice.pdf", Date),
        "sidecar-docx" => new ArchiveSidecar("Roof offer", "Contractor offer for the roof repair", Slug.Parse("zyggy"), ArchiveMediaType.Docx, 1289,
            "fa0d4998a5b647d23edf5ec41eaa6a1192341017c46d5f465a4208fc5345e0ca", Date, "sample.docx", Date),
        "sidecar-xlsx" => new ArchiveSidecar("Roof budget", "Cost lines for the roof repair", Slug.Parse("zyggy"), ArchiveMediaType.Xlsx, 2262,
            "3c6625560fa0b5b60c3c704353460a841f34938af9701bc423f4ac0cb1ffa2f9", Date, "sample.xlsx", Date),
        "sidecar-pptx" => new ArchiveSidecar("Roof pitch", "Slides shown to the contractor", Slug.Parse("zyggy"), ArchiveMediaType.Pptx, 1335,
            "aa100f3a2150a877f29c06db427e70bd53e65759d9e80c1e493bd90a7a4e25b5", Date, "sample.pptx", Date),
        _ => throw new ArgumentOutOfRangeException(nameof(golden)),
    };
}
