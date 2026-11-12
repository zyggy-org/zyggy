using Zyggy.Core.Registry;

namespace Zyggy.Core.Tests.Registry;

public sealed class RegistryTextTests
{
    [Fact]
    public void Clean_RemovesCcAndLineSeparators()
    {
        // Arrange: tab, bell, LF, CR, line separator, paragraph separator, next line
        string text = string.Concat("a", (char)0x09, "b", (char)0x07, "c", (char)0x0A, "d", (char)0x0D, (char)0x2028, "e", (char)0x2029, "f", (char)0x85, "g");

        // Act
        string cleaned = RegistryText.Clean(text);

        // Assert
        cleaned.Should().Be("abcdefg");
    }

    [Fact]
    public void Clean_150CutNeverSplitsSurrogatePair()
    {
        // Arrange: the emoji's high surrogate sits at index 149, its low surrogate at 150
        string text = new string('x', 149) + char.ConvertFromUtf32(0x1F600) + "tail";

        // Act
        string cleaned = RegistryText.Clean(text);

        // Assert
        cleaned.Should().Be(new string('x', 149));
    }

    [Fact]
    public void Clean_LongText_CutTo150()
    {
        // Act + Assert
        RegistryText.Clean(new string('y', 400)).Should().HaveLength(150);
    }

    [Fact]
    public void Clean_ShortText_Unchanged()
    {
        // Act + Assert
        RegistryText.Clean("Writes and verifies EF Core migrations").Should().Be("Writes and verifies EF Core migrations");
    }
}
