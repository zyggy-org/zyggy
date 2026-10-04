using System.Text;

using Zyggy.Core.Memory;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Memory;

public sealed class MemoryFileTests
{
    private const string Sample =
        "---\nname: Acme Corp\ndescription: \"Acme Corp: client since 2025\"\naliases: [acme, acme-corporation]\nupdated: 2026-10-01\nowner: alice\n---\n" +
        "- [observed] 2026-10-01 [m365-mail 2026-10-01]: Acme Corp renewed the contract.\n- [stated] 2026-10-02: Acme pays late.\n";

    [Fact]
    public void Read_FrontMatterAndBody_ParsesFields()
    {
        // Act
        var file = MemoryFileReader.Parse(Sample);

        // Assert
        file.HasFrontMatter.Should().BeTrue();
        file.Name.Should().Be("Acme Corp");
        file.Description.Should().Be("Acme Corp: client since 2025");
        file.Aliases.Should().Equal("acme", "acme-corporation");
        file.Updated.Should().Be(new DateOnly(2026, 10, 1));
        file.UnknownKeys.Should().ContainKey("owner").WhoseValue.Should().Be("alice");
        file.BodyLines.Should().Equal(
            "- [observed] 2026-10-01 [m365-mail 2026-10-01]: Acme Corp renewed the contract.",
            "- [stated] 2026-10-02: Acme pays late.");
    }

    [Fact]
    public void Write_ThenRead_RoundTrips()
    {
        // Arrange
        using var tree = new MemoryTree();
        var path = tree.Full("business/clients/acme-corp.md");
        var original = MemoryFileReader.Parse(Sample) with { Updated = new DateOnly(2026, 10, 4) };

        // Act
        MemoryFileWriter.Write(path, original);
        var back = MemoryFileReader.Read(path);

        // Assert
        back.Name.Should().Be(original.Name);
        back.Description.Should().Be(original.Description);
        back.Aliases.Should().Equal(original.Aliases);
        back.Updated.Should().Be(new DateOnly(2026, 10, 4));
        back.UnknownKeys.Should().Equal(original.UnknownKeys);
        back.BodyLines.Should().Equal(original.BodyLines);
    }

    [Fact]
    public void Write_NoFrontMatterFile_KeepsBodyOnly()
    {
        // Arrange
        using var tree = new MemoryTree();
        var path = tree.Full("business/clients/globex.md");
        var file = MemoryFileReader.Parse("- [stated] 2026-09-02: Globex asked for a quote.\n");

        // Act
        MemoryFileWriter.Write(path, file);

        // Assert
        file.HasFrontMatter.Should().BeFalse();
        File.ReadAllText(path).Should().Be("- [stated] 2026-09-02: Globex asked for a quote.\n");
    }

    [Fact]
    public void Write_LeavesNoTempFileAndUsesLf()
    {
        // Arrange
        using var tree = new MemoryTree();
        var path = tree.Full("private/areas/marathon.md");
        var file = new MemoryFile("marathon", "Ghent marathon", [], new DateOnly(2026, 9, 20), new Dictionary<string, string>(), true,
            ["- [stated] 2026-09-20: Race in April."]);

        // Act
        MemoryFileWriter.Write(path, file);

        // Assert
        var bytes = File.ReadAllBytes(path);
        bytes.Should().NotContain((byte)'\r');
        bytes.Take(3).Should().NotEqual(new byte[] { 0xEF, 0xBB, 0xBF });
        bytes[^1].Should().Be((byte)'\n');
        Encoding.UTF8.GetString(bytes).Should().StartWith("---\nname: marathon\n");
        Directory.EnumerateFiles(Path.GetDirectoryName(path)!).Should().ContainSingle();
    }
}
